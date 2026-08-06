using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ProjectSoundboard.Audio.Dsp;
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

    public int Read(float[] buffer, int offset, int count)
    {
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

    /// <summary>Number of voices currently mixed into this bus.</summary>
    public int VoiceCount
    {
        get { lock (_gate) return _mixer?.MixerInputs.Count() ?? 0; }
    }

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
                _chain = new MasterChain(_mixer) { Volume = _volume, Muted = _muted };

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

    public void AddVoice(ISampleProvider voice)
    {
        lock (_gate)
        {
            if (_mixer is null) return;
            _mixer.AddMixerInput(voice);
        }
    }

    public void RemoveVoice(ISampleProvider voice)
    {
        lock (_gate)
        {
            try { _mixer?.RemoveMixerInput(voice); }
            catch { /* already removed by the mixer when it ended */ }
        }
    }

    public void RemoveAllVoices()
    {
        lock (_gate) _mixer?.RemoveAllMixerInputs();
    }

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
