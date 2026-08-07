namespace ProjectSoundboard.Audio.Playback;

/// <summary>
/// One logical "sound playing", which may be up to two voices — one feeding the virtual
/// microphone and one feeding the user's own speakers. Controls apply to both together.
/// </summary>
public sealed class PlaybackHandle
{
    private readonly List<VoiceBase> _voices = new();
    private readonly Lock _gate = new();
    private int _finishedCount;
    private bool _completed;

    public PlaybackHandle(string soundId, string filePath, string displayName)
    {
        SoundId = soundId;
        FilePath = filePath;
        DisplayName = displayName;
        StartedUtc = DateTime.UtcNow;
    }

    public string SoundId { get; }
    public string FilePath { get; }
    public string DisplayName { get; }
    public DateTime StartedUtc { get; }

    /// <summary>Raised once every voice has ended.</summary>
    public event EventHandler? Completed;

    public bool IsCompleted => _completed;

    public bool IsPaused
    {
        get { lock (_gate) return _voices.Count > 0 && _voices.All(v => v.IsPaused); }
    }

    /// <summary>
    /// Report from a voice that is still running. If one output bus was switched off
    /// mid-playback its voice is abandoned and frozen, and reading the position from that
    /// one would make the progress bar stick while the sound is still audible elsewhere.
    /// </summary>
    private VoiceBase? ReferenceVoice
    {
        get
        {
            lock (_gate)
            {
                if (_voices.Count == 0) return null;
                return _voices.FirstOrDefault(v => !v.IsFinished) ?? _voices[0];
            }
        }
    }

    public double PositionSeconds => ReferenceVoice?.PositionSeconds ?? 0;

    public double LengthSeconds => ReferenceVoice?.LengthSeconds ?? 0;

    public double Progress
    {
        get
        {
            var length = LengthSeconds;
            return length <= 0 ? 0 : Math.Clamp(PositionSeconds / length, 0, 1);
        }
    }

    internal void Attach(VoiceBase voice)
    {
        lock (_gate) _voices.Add(voice);

        voice.Finished += (_, _) =>
        {
            int total;
            lock (_gate) total = _voices.Count;

            if (Interlocked.Increment(ref _finishedCount) < total) return;
            if (_completed) return;

            _completed = true;
            Completed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>The individual voices, one per output bus. Exposed so the editor can seek them.</summary>
    public IReadOnlyList<VoiceBase> Voices
    {
        get { lock (_gate) return _voices.ToArray(); }
    }

    public void Pause()
    {
        foreach (var v in Voices) v.Pause();
    }

    public void Resume()
    {
        foreach (var v in Voices) v.Resume();
    }

    public void Stop(int fadeMs = 30)
    {
        foreach (var v in Voices) v.Stop(fadeMs);
    }

    public void Restart()
    {
        foreach (var v in Voices) v.Restart();
    }

    /// <summary>Scrub to a position. Safe to call as fast as the mouse moves.</summary>
    public void Seek(double seconds)
    {
        foreach (var v in Voices) v.RequestSeek(seconds);
    }

    public void SetVolume(float volume)
    {
        foreach (var v in Voices) v.SetVolume(volume);
    }

    public void SetSpeed(float speed)
    {
        foreach (var v in Voices) v.SetSpeed(speed);
    }

    /// <summary>Start or stop looping without interrupting what is already playing.</summary>
    public void SetLoop(bool loop)
    {
        foreach (var v in Voices) v.SetLoop(loop);
    }

    /// <summary>Live multiplier used by ducking and the soundboard mute hotkey.</summary>
    internal void SetExternalGain(float gain)
    {
        foreach (var v in Voices) v.ExternalGain = gain;
    }

    internal void DisposeVoices()
    {
        foreach (var v in Voices)
        {
            if (v is IDisposable d)
            {
                try { d.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
