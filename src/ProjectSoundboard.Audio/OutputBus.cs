using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ProjectSoundboard.Audio.Dsp;
using ProjectSoundboard.Audio.Playback;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Audio;

/// <summary>
/// The master processing chain sitting between the mixer and the sound card:
/// EQ → compressor → limiter → volume → meter.
/// </summary>
internal sealed class MasterChain : ISampleProvider
{
    private readonly ISampleProvider _source;

    public MasterChain(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;

        Equalizer.Configure(WaveFormat.SampleRate, WaveFormat.Channels);
        Compressor.Configure(WaveFormat.SampleRate);
        Limiter.Configure(WaveFormat.SampleRate);
    }

    public WaveFormat WaveFormat { get; }

    public Equalizer Equalizer { get; } = new();
    public Compressor Compressor { get; } = new();
    public Limiter Limiter { get; } = new();
    public LevelMeter Meter { get; } = new();

    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }

    /// <summary>Name of the bus, only so a late-callback warning can say which one.</summary>
    public string Name { get; init; } = "output";

    // ---- deadline watching -------------------------------------------------
    //
    // The sound card asks for the next block of audio on a schedule. If we are handed back
    // control late — because the machine is busy and this thread did not get the CPU in time
    // — the card has already run out and what comes out is a break in the sound.
    //
    // Nothing measured that, so "it stutters when my computer is busy" could not be told
    // apart from the decoder failing to keep up, which is a completely different problem
    // with a completely different fix. Now the gap between calls is compared against how
    // much audio was handed over, and falling behind is counted.

    private long _lastCallTicks;
    private int _lastFrames;
    private int _lateCallbacks;
    private long _summaryTicks;

    /// <summary>Below this the block was effectively silence, so nobody could have heard a gap.</summary>
    private const float Audible = 0.0005f;

    /// <summary>Called after processing, so the meter can say whether this block was audible.</summary>
    private void WatchDeadline(int framesDelivered)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        if (_lastCallTicks != 0 && _lastFrames > 0)
        {
            var gap = System.Diagnostics.Stopwatch.GetElapsedTime(_lastCallTicks, now);
            var owed = TimeSpan.FromSeconds(_lastFrames / (double)WaveFormat.SampleRate);

            // Three times the audio handed over, plus slack. Being handed back late by a
            // few milliseconds is ordinary and inaudible; measuring it at twice-plus-8ms
            // counted normal jitter on a completely idle machine.
            //
            // And only while something is actually audible. A late callback during silence
            // cannot be heard by anyone, and counting it would turn this into noise in the
            // log rather than evidence of the thing being complained about.
            if (Meter.Peak > Audible &&
                gap > owed + owed + owed + TimeSpan.FromMilliseconds(15))
            {
                _lateCallbacks++;
            }
        }

        _lastCallTicks = now;
        _lastFrames = framesDelivered;

        if (_summaryTicks == 0) _summaryTicks = now;

        // Summarised rather than reported one by one: this runs hundreds of times a second
        // and a log line per glitch would be its own performance problem.
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_summaryTicks, now) < TimeSpan.FromSeconds(30)) return;

        var late = _lateCallbacks;
        _lateCallbacks = 0;
        _summaryTicks = now;

        // A handful over half a minute is not worth mentioning; a run of them is.
        if (late >= 5)
        {
            Log.Warn($"{Name}: the sound card was left waiting {late} time(s) in the last 30 " +
                     "seconds while audio was playing. That is what a break in the sound is — " +
                     "something else on the machine is taking the processor at the wrong moment.");
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // This runs on the render thread, which belongs to NAudio — so this is the only
        // place we can reach it to tell Windows it is audio work. After the first call it
        // is a thread-static check and nothing else.
        MmcssThread.EnsureRegistered("Pro Audio", high: true);

        var read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        var channels = WaveFormat.Channels;

        Equalizer.Process(buffer, offset, read, channels);
        Compressor.Process(buffer, offset, read, channels);

        var gain = Muted ? 0f : Volume;
        if (Math.Abs(gain - 1f) > 0.0001f)
        {
            for (var i = 0; i < read; i++) buffer[offset + i] *= gain;
        }

        // Limiter last so nothing downstream can push the signal back over full scale.
        Limiter.Process(buffer, offset, read, channels);
        Meter.Process(buffer, offset, read);

        // Last, so it can ask the meter whether this block was audible at all.
        WatchDeadline(read / channels);

        return read;
    }
}

/// <summary>
/// One physical destination — the virtual microphone cable or the user's headphones.
/// Voices are added to its mixer and removed automatically when they finish.
/// </summary>
public sealed class OutputBus : IDisposable
{
    private readonly Lock _gate = new();

    private WasapiOut? _output;
    private MixingSampleProvider? _mixer;
    private MasterChain? _chain;
    private MMDevice? _device;

    /// <summary>
    /// Everything currently mixed in, so it can be released if the bus goes away. Concurrent
    /// because the render thread removes from it, and making that thread wait on a lock is
    /// what deadlocked the application.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ISampleProvider, byte> _voices = new();

    public OutputBus(string name) => Name = name;

    public string Name { get; }

    public bool IsRunning { get; private set; }

    /// <summary>Friendly name of the device currently in use, for the UI.</summary>
    public string? DeviceName { get; private set; }

    public string? DeviceId { get; private set; }

    /// <summary>Requested buffer latency in milliseconds.</summary>
    public int LatencyMs { get; private set; }

    public WaveFormat? Format { get; private set; }

    /// <summary>Last error message, surfaced in the audio settings page.</summary>
    public string? LastError { get; private set; }

    public LevelMeter? Meter => _chain?.Meter;
    public Limiter? Limiter => _chain?.Limiter;
    public Compressor? Compressor => _chain?.Compressor;
    public Equalizer? Equalizer => _chain?.Equalizer;

    private float _volume = 1f;
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 2f);
            if (_chain is not null) _chain.Volume = _volume;
        }
    }

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (_chain is not null) _chain.Muted = value;
        }
    }

    /// <summary>
    /// Number of voices currently mixed into this bus. Counted from our own list rather than
    /// by asking the mixer, which would mean touching the mixer's lock from whichever thread
    /// happens to want a number.
    /// </summary>
    public int VoiceCount => _voices.Count;

    /// <summary>
    /// (Re)open the bus on <paramref name="device"/>. Any voices playing on the previous
    /// device are dropped — a device switch is inherently a hard cut.
    /// </summary>
    public bool Start(MMDevice? device, int sampleRate, int channels, int latencyMs)
    {
        Stop();

        if (device is null)
        {
            LastError = "No audio device selected.";
            return false;
        }

        lock (_gate)
        {
            try
            {
                _device = device;
                Format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
                LatencyMs = Math.Clamp(latencyMs, 2, 500);

                _mixer = new MixingSampleProvider(Format) { ReadFully = true };
                _mixer.MixerInputEnded += OnMixerInputEnded;
                _chain = new MasterChain(_mixer) { Volume = _volume, Muted = _muted, Name = Name };

                // Shared mode on purpose: exclusive mode would lock other applications out
                // of the virtual cable, which is exactly what we need it to share.
                _output = new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMs);
                _output.PlaybackStopped += OnPlaybackStopped;
                _output.Init(_chain);
                _output.Play();

                DeviceName = device.FriendlyName;
                DeviceId = device.ID;
                IsRunning = true;
                LastError = null;

                Log.Info($"{Name} bus started on '{DeviceName}' " +
                         $"({sampleRate} Hz, {channels} ch, {LatencyMs} ms buffer).");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error($"{Name} bus failed to start on '{device.FriendlyName}'", ex);
                CleanUp();
                return false;
            }
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        LastError = e.Exception.Message;
        Log.Error($"{Name} bus stopped unexpectedly", e.Exception);
        IsRunning = false;
    }

    // The mixer has a lock of its own, and it holds it while reading — which is when it
    // tells us an input has ended. So there are two locks and two threads:
    //
    //   this thread   holds _gate, then asks the mixer to add an input
    //   render thread holds the mixer, then tells us the input ended and asks for _gate
    //
    // Each waits for what the other is holding, and the app stops dead. Pressing Next with
    // shuffle on did it every time, because that stops one sound at the same moment as it
    // starts another, which is exactly the pair of operations involved.
    //
    // The rule from here: never call the mixer while holding _gate, and never take _gate on
    // the render thread. Our own list of voices is concurrent so the callback needs no lock
    // at all.

    /// <summary>Snapshot of the mixer, so it can be used after the lock is released.</summary>
    private MixingSampleProvider? CurrentMixer
    {
        get { lock (_gate) return _mixer; }
    }

    public void AddVoice(ISampleProvider voice)
    {
        var mixer = CurrentMixer;
        if (mixer is null) return;

        _voices.TryAdd(voice, 0);
        mixer.AddMixerInput(voice);

        // The bus can be stopped while we are outside the lock. If it has been, this voice
        // is attached to a mixer nobody will ever read, so let go of it — otherwise the
        // handle that owns it waits for ever for a sound that cannot play.
        if (ReferenceEquals(CurrentMixer, mixer)) return;

        _voices.TryRemove(voice, out _);
        if (voice is VoiceBase playable) playable.Abandon();
    }

    public void RemoveVoice(ISampleProvider voice)
    {
        _voices.TryRemove(voice, out _);

        try { CurrentMixer?.RemoveMixerInput(voice); }
        catch { /* already removed by the mixer when it ended */ }
    }

    public void RemoveAllVoices()
    {
        _voices.Clear();
        CurrentMixer?.RemoveAllMixerInputs();
    }

    /// <summary>
    /// The mixer drops inputs that finish on their own; keep our list in step. Runs on the
    /// render thread with the mixer's lock held, so it must not wait for anything.
    /// </summary>
    private void OnMixerInputEnded(object? sender, SampleProviderEventArgs e) =>
        _voices.TryRemove(e.SampleProvider, out _);

    /// <summary>
    /// True when the bus is already running exactly this configuration, so there is no
    /// reason to tear it down. Restarting a bus cuts off whatever it is playing, and the
    /// user changing an unrelated setting should not silence the sound they are hearing.
    /// </summary>
    public bool Matches(string? deviceId, int sampleRate, int channels, int latencyMs) =>
        IsRunning
        && DeviceId == deviceId
        && Format is not null
        && Format.SampleRate == sampleRate
        && Format.Channels == channels
        && LatencyMs == latencyMs;

    public void Stop()
    {
        lock (_gate)
        {
            if (_output is not null)
            {
                try
                {
                    _output.PlaybackStopped -= OnPlaybackStopped;
                    _output.Stop();
                }
                catch (Exception ex) { Log.Debug($"{Name} stop: {ex.Message}"); }
            }

            CleanUp();
        }
    }

    private void CleanUp()
    {
        try { _output?.Dispose(); } catch { /* ignore */ }

        // Nothing will ever read these again, so tell them so. Otherwise the handles that
        // own them wait forever for a completion that cannot arrive.
        foreach (var voice in _voices.Keys)
        {
            if (voice is VoiceBase playable) playable.Abandon();
        }

        _voices.Clear();

        if (_mixer is not null) _mixer.MixerInputEnded -= OnMixerInputEnded;

        _output = null;
        _mixer = null;
        _chain = null;
        _device = null;
        IsRunning = false;
        DeviceName = null;
        DeviceId = null;
    }

    public void Dispose() => Stop();
}
