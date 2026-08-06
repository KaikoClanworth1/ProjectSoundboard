using NAudio.Wave;
using ProjectSoundboard.Audio.Playback;
using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Audio;

/// <summary>Where a triggered sound should be heard.</summary>
[Flags]
public enum PlayTarget
{
    None = 0,
    /// <summary>The virtual cable that voice apps see as a microphone.</summary>
    VirtualMic = 1,
    /// <summary>The user's own headphones or speakers.</summary>
    Monitor = 2,
    Both = VirtualMic | Monitor
}

/// <summary>
/// The heart of the soundboard: owns both output buses, decides which voices get created
/// for a trigger, and keeps everything in sync with the user's settings.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly SettingsService _settings;
    private readonly AudioDeviceService _devices;
    private readonly List<PlaybackHandle> _active = new();
    private readonly Lock _gate = new();
    private readonly System.Threading.Timer _tick;

    private bool _disposed;

    public AudioEngine(SettingsService settings, AudioDeviceService devices)
    {
        _settings = settings;
        _devices = devices;

        VirtualMicBus = new OutputBus("Virtual mic");
        MonitorBus = new OutputBus("Monitor");

        // Drives progress bars and prunes finished handles.
        _tick = new System.Threading.Timer(_ => Tick(), null, 100, 100);
    }

    public OutputBus VirtualMicBus { get; }
    public OutputBus MonitorBus { get; }
    public SoundCache Cache { get; } = new();

    /// <summary>Raised when a sound starts, stops or finishes.</summary>
    public event EventHandler? PlaybackChanged;

    /// <summary>Raised ~10x/second while anything is playing, for progress UI.</summary>
    public event EventHandler? Tick10Hz;

    /// <summary>Raised when a sound could not be played, with a user-facing reason.</summary>
    public event EventHandler<string>? PlaybackFailed;

    public bool SoundboardMuted { get; set; }

    public IReadOnlyList<PlaybackHandle> Active
    {
        get { lock (_gate) return _active.ToArray(); }
    }

    public bool IsAnythingPlaying
    {
        get { lock (_gate) return _active.Count > 0; }
    }

    /// <summary>Combined round-trip estimate shown in the audio settings page.</summary>
    public int EstimatedLatencyMs
    {
        get
        {
            var buffer = VirtualMicBus.IsRunning ? VirtualMicBus.LatencyMs : MonitorBus.LatencyMs;
            // WASAPI shared mode adds roughly one extra period on top of our buffer.
            return buffer + Math.Max(3, buffer / 2);
        }
    }

    // -----------------------------------------------------------------------
    // Device lifecycle
    // -----------------------------------------------------------------------

    /// <summary>Open (or reopen) both buses from the current settings.</summary>
    public void ApplyAudioSettings()
    {
        var audio = _settings.Settings.Audio;
        var perf = _settings.Settings.Performance;

        var sampleRate = audio.SampleRate is 44100 or 48000 or 96000 ? audio.SampleRate : 48000;
        var channels = audio.Channels is 1 or 2 ? audio.Channels : 2;

        // Low latency mode requests the smallest buffer we are willing to ask for.
        var latency = audio.LowLatencyMode
            ? Math.Clamp(audio.BufferSizeMs, 5, 40)
            : Math.Clamp(audio.BufferSizeMs, 10, 200);

        Cache.Configure(sampleRate, channels);
        Cache.BudgetBytes = Math.Max(32, perf.ImageCacheMb) * 1024L * 1024L;

        if (audio.VirtualMicEnabled)
        {
            var device = _devices.Resolve(audio.VirtualMicDeviceId, DeviceKind.Output);
            VirtualMicBus.Start(device, sampleRate, channels, latency);
        }
        else
        {
            VirtualMicBus.Stop();
        }

        if (audio.MonitorEnabled)
        {
            var device = _devices.Resolve(audio.MonitorDeviceId, DeviceKind.Output);
            MonitorBus.Start(device, sampleRate, channels, latency);
        }
        else
        {
            MonitorBus.Stop();
        }

        VirtualMicBus.Volume = audio.VirtualMicVolume * audio.MasterVolume;
        MonitorBus.Volume = audio.MonitorVolume * audio.MasterVolume;

        ApplyProcessingSettings();
    }

    /// <summary>Push EQ / dynamics settings into both buses without reopening devices.</summary>
    public void ApplyProcessingSettings()
    {
        var audio = _settings.Settings.Audio;

        foreach (var bus in new[] { VirtualMicBus, MonitorBus })
        {
            if (bus.Limiter is { } limiter)
            {
                limiter.Enabled = audio.LimiterEnabled;
                limiter.ThresholdDb = audio.LimiterThresholdDb;
            }

            if (bus.Compressor is { } comp)
            {
                comp.Enabled = audio.CompressorEnabled;
                comp.ThresholdDb = audio.CompressorThresholdDb;
                comp.Ratio = audio.CompressorRatio;
                comp.AttackMs = audio.CompressorAttackMs;
                comp.ReleaseMs = audio.CompressorReleaseMs;
                comp.MakeupGainDb = audio.CompressorMakeupDb;
            }

            if (bus.Equalizer is { } eq)
            {
                eq.Enabled = audio.EqEnabled;
                eq.SetBands(audio.EqBandsDb);
            }
        }

        VirtualMicBus.Volume = audio.VirtualMicVolume * audio.MasterVolume;
        MonitorBus.Volume = audio.MonitorVolume * audio.MasterVolume;
    }

    // -----------------------------------------------------------------------
    // Playback
    // -----------------------------------------------------------------------

    /// <summary>
    /// Trigger a sound. Returns null when nothing could be started (missing file,
    /// unsupported codec, or the simultaneous-sound cap was reached).
    /// </summary>
    public PlaybackHandle? Play(SoundEntry entry, PlayTarget target = PlayTarget.Both)
    {
        if (_disposed) return null;

        var playback = _settings.Settings.Playback;

        if (!File.Exists(entry.FilePath))
        {
            Fail($"'{entry.DisplayName}' could not be found on disk.");
            return null;
        }

        // ---- playback mode ------------------------------------------------
        switch (playback.Mode)
        {
            case PlaybackMode.Solo:
                StopAll(playback.GlobalFadeOutMs);
                break;

            case PlaybackMode.Restart:
            {
                var existing = Active.FirstOrDefault(h => h.SoundId == entry.Id);
                if (existing is not null)
                {
                    existing.Restart();
                    PlaybackChanged?.Invoke(this, EventArgs.Empty);
                    return existing;
                }
                break;
            }

            case PlaybackMode.Overlap:
                if (playback.StopOnSecondPress)
                {
                    var existing = Active.FirstOrDefault(h => h.SoundId == entry.Id);
                    if (existing is not null)
                    {
                        existing.Stop(playback.GlobalFadeOutMs);
                        return null;
                    }
                }
                break;
        }

        lock (_gate)
        {
            if (_active.Count >= Math.Max(1, playback.MaxSimultaneousSounds))
            {
                // Steal the oldest voice rather than refusing to play — a soundboard
                // that silently ignores a key press feels broken.
                var oldest = _active.OrderBy(h => h.StartedUtc).First();
                oldest.Stop(20);
            }
        }

        var settings = new VoiceSettings
        {
            Volume = entry.Volume,
            Speed = entry.Speed,
            Loop = entry.Loop,
            FadeInMs = entry.FadeInMs,
            FadeOutMs = entry.FadeOutMs,
            TrimStartMs = entry.TrimStartMs,
            TrimEndMs = entry.TrimEndMs,
            Normalize = entry.Normalize,
            PeakAmplitude = entry.PeakAmplitude
        };

        var handle = new PlaybackHandle(entry.Id, entry.FilePath, entry.DisplayName);
        var started = false;

        if (target.HasFlag(PlayTarget.VirtualMic) && VirtualMicBus.IsRunning)
            started |= AddVoice(handle, VirtualMicBus, entry.FilePath, settings);

        if (target.HasFlag(PlayTarget.Monitor) && MonitorBus.IsRunning)
            started |= AddVoice(handle, MonitorBus, entry.FilePath, settings);

        if (!started)
        {
            Fail(VirtualMicBus.IsRunning || MonitorBus.IsRunning
                ? $"'{entry.DisplayName}' could not be decoded."
                : "No output device is running. Check Audio settings.");
            return null;
        }

        if (SoundboardMuted) handle.SetExternalGain(0f);

        lock (_gate) _active.Add(handle);

        handle.Completed += (_, _) =>
        {
            lock (_gate) _active.Remove(handle);
            handle.DisposeVoices();
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
        };

        PlaybackChanged?.Invoke(this, EventArgs.Empty);
        return handle;
    }

    /// <summary>Play only through the monitor, used by the editor's preview button.</summary>
    public PlaybackHandle? Preview(SoundEntry entry) => Play(entry, PlayTarget.Monitor);

    private bool AddVoice(PlaybackHandle handle, OutputBus bus, string path, VoiceSettings settings)
    {
        if (bus.Format is not { } format) return false;

        try
        {
            VoiceBase voice;

            var cached = Cache.GetOrLoad(path);
            if (cached is not null)
            {
                // Reuse the analysis we already did when decoding.
                if (settings.Normalize && settings.PeakAmplitude <= 0.0001f)
                    settings = With(settings, cached.Peak);

                voice = new CachedVoice(cached, settings, format);
            }
            else
            {
                voice = new StreamingVoice(path, settings, format);
            }

            handle.Attach(voice);
            bus.AddVoice(voice);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not start '{Path.GetFileName(path)}' on {bus.Name}: {ex.Message}");
            return false;
        }
    }

    private static VoiceSettings With(VoiceSettings settings, float peak)
    {
        var copy = settings.Clone();
        copy.PeakAmplitude = peak;
        return copy;
    }

    public void StopAll(int fadeMs = -1)
    {
        if (fadeMs < 0) fadeMs = _settings.Settings.Playback.GlobalFadeOutMs;
        foreach (var handle in Active) handle.Stop(fadeMs);
    }

    public void StopSound(string soundId, int fadeMs = -1)
    {
        if (fadeMs < 0) fadeMs = _settings.Settings.Playback.GlobalFadeOutMs;
        foreach (var handle in Active.Where(h => h.SoundId == soundId)) handle.Stop(fadeMs);
    }

    public void PauseAll()
    {
        foreach (var handle in Active) handle.Pause();
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResumeAll()
    {
        foreach (var handle in Active) handle.Resume();
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pause everything if anything is running, otherwise resume.</summary>
    public void TogglePauseAll()
    {
        var handles = Active;
        if (handles.Count == 0) return;

        if (handles.Any(h => !h.IsPaused)) PauseAll();
        else ResumeAll();
    }

    public void SetSoundboardMuted(bool muted)
    {
        SoundboardMuted = muted;
        var gain = muted ? 0f : 1f;
        foreach (var handle in Active) handle.SetExternalGain(gain);
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Master volume, 0..1, applied to both buses immediately.</summary>
    public void SetMasterVolume(float volume)
    {
        var audio = _settings.Settings.Audio;
        audio.MasterVolume = Math.Clamp(volume, 0f, 1f);
        VirtualMicBus.Volume = audio.VirtualMicVolume * audio.MasterVolume;
        MonitorBus.Volume = audio.MonitorVolume * audio.MasterVolume;
    }

    private void Fail(string message)
    {
        Log.Warn(message);
        PlaybackFailed?.Invoke(this, message);
    }

    private void Tick()
    {
        if (_disposed) return;

        // Voices that stopped without their mixer input being removed (device dropped
        // mid-playback) would otherwise linger forever.
        List<PlaybackHandle> stale;
        lock (_gate)
        {
            stale = _active.Where(h => h.IsCompleted).ToList();
            foreach (var h in stale) _active.Remove(h);
        }

        foreach (var h in stale) h.DisposeVoices();

        if (IsAnythingPlaying || stale.Count > 0)
            Tick10Hz?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Warm the cache for the sounds the user is most likely to hit next.</summary>
    public Task PreloadAsync(IEnumerable<SoundEntry> entries, CancellationToken ct = default)
    {
        var perf = _settings.Settings.Performance;
        if (!perf.PreloadFrequentSounds) return Task.CompletedTask;

        var paths = entries
            .Where(e => !e.IsMissing && !e.IsBroken)
            .OrderByDescending(e => e.IsFavorite)
            .ThenByDescending(e => e.PlayCount)
            .Take(Math.Max(1, perf.PreloadCount))
            .Select(e => e.FilePath);

        return Cache.PreloadAsync(paths, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _tick.Dispose();
        StopAll(0);

        foreach (var handle in Active) handle.DisposeVoices();
        lock (_gate) _active.Clear();

        VirtualMicBus.Dispose();
        MonitorBus.Dispose();
        Cache.Clear();
    }
}
