using NAudio.Wave;

namespace ProjectSoundboard.Audio.Playback;

/// <summary>Per-sound playback parameters, snapshotted when a voice starts.</summary>
public sealed class VoiceSettings
{
    public float Volume { get; set; } = 1f;
    public float Speed { get; set; } = 1f;
    public bool Loop { get; set; }
    public int FadeInMs { get; set; }
    public int FadeOutMs { get; set; }
    public int TrimStartMs { get; set; }
    public int TrimEndMs { get; set; }
    public bool Normalize { get; set; }

    /// <summary>Measured peak of the source, 0 = unknown (normalisation is then skipped).</summary>
    public float PeakAmplitude { get; set; }

    public VoiceSettings Clone() => (VoiceSettings)MemberwiseClone();
}

/// <summary>
/// One playing instance of a sound on one output bus. Handles trimming, looping, speed,
/// fades and gain; subclasses only have to hand over raw frames.
/// </summary>
public abstract class VoiceBase : ISampleProvider
{
    private readonly float[] _frameA;
    private readonly float[] _frameB;
    private readonly float[] _scratch;

    private double _fraction;
    private bool _primed;

    // Written from the audio thread and from Abandon() on the UI thread.
    private volatile bool _finished;
    private int _finishedRaised;

    private long _outputFrame;
    private long _fadeInFrames;
    private long _fadeOutFrames;
    private long _totalOutputFrames = -1;

    private int _releaseTotal;
    private int _releaseRemaining;
    private bool _releasing;

    private float _baseGain = 1f;
    private volatile bool _paused;

    protected readonly int ChannelCount;
    protected readonly int SampleRate;
    protected readonly VoiceSettings Settings;

    protected VoiceBase(WaveFormat format, VoiceSettings settings)
    {
        WaveFormat = format;
        ChannelCount = format.Channels;
        SampleRate = format.SampleRate;
        Settings = settings;

        _frameA = new float[ChannelCount];
        _frameB = new float[ChannelCount];
        _scratch = new float[ChannelCount];

        Speed = Math.Clamp(settings.Speed, 0.25f, 4f);
        _fadeInFrames = MsToFrames(settings.FadeInMs);
        _fadeOutFrames = MsToFrames(settings.FadeOutMs);

        _baseGain = Math.Clamp(settings.Volume, 0f, 4f);

        if (settings.Normalize && settings.PeakAmplitude > 0.0001f)
        {
            // Bring the loudest sample to -1 dBFS, but never boost by more than 20 dB
            // or quiet recordings turn into a wall of amplified noise.
            var boost = Math.Min(0.891f / settings.PeakAmplitude, 10f);
            _baseGain *= boost;
        }
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Playback rate. 1 = normal; changing it also changes pitch, like a tape.</summary>
    public float Speed { get; private set; }

    /// <summary>Raised once, on the audio thread, when the voice runs out or is stopped.</summary>
    public event EventHandler? Finished;

    public bool IsFinished => _finished;
    public bool IsPaused => _paused;

    /// <summary>Extra multiplier applied live (used for ducking and master mutes).</summary>
    public float ExternalGain { get; set; } = 1f;

    /// <summary>Position within the source file, in seconds.</summary>
    public abstract double PositionSeconds { get; }

    /// <summary>Length of the trimmed region, in seconds. 0 when unknown.</summary>
    public abstract double LengthSeconds { get; }

    protected long MsToFrames(int ms) => (long)(ms / 1000.0 * SampleRate);

    /// <summary>
    /// Set once the total output length is known, enabling the fade-out ramp.
    /// Looping voices leave this at -1.
    /// </summary>
    protected void SetTotalOutputFrames(long sourceFrames)
    {
        if (Settings.Loop) { _totalOutputFrames = -1; return; }
        _totalOutputFrames = Speed <= 0 ? sourceFrames : (long)(sourceFrames / Speed);
    }

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    /// <summary>Ramp to silence over <paramref name="fadeMs"/> then finish.</summary>
    public void Stop(int fadeMs = 30)
    {
        if (_finished) return;

        if (fadeMs <= 0)
        {
            _finished = true;
            return;
        }

        if (_releasing) return;

        _releaseTotal = Math.Max(1, (int)MsToFrames(fadeMs));
        _releaseRemaining = _releaseTotal;
        _releasing = true;
        _paused = false; // a paused voice must still be able to be stopped
    }

    /// <summary>Jump back to the start of the trimmed region.</summary>
    public virtual void Restart()
    {
        _outputFrame = 0;
        _fraction = 0;
        _primed = false;
        _releasing = false;
        _paused = false;
        SeekToStart();
    }

    protected abstract void SeekToStart();

    /// <summary>
    /// Fill one frame of source audio. Return false at the end of the (trimmed, possibly
    /// looping) region.
    /// </summary>
    protected abstract bool ReadSourceFrame(float[] destination);

    public int Read(float[] buffer, int offset, int count)
    {
        var framesWanted = count / ChannelCount;
        var written = 0;

        while (written < framesWanted)
        {
            if (_finished) break;

            var index = offset + written * ChannelCount;

            if (_paused)
            {
                for (var c = 0; c < ChannelCount; c++) buffer[index + c] = 0f;
                written++;
                continue;
            }

            if (!NextFrame(_scratch))
            {
                _finished = true;
                break;
            }

            var gain = CurrentGain();
            for (var c = 0; c < ChannelCount; c++) buffer[index + c] = _scratch[c] * gain;

            _outputFrame++;
            written++;
        }

        if (_finished) RaiseFinishedOnce();

        // Returning fewer samples than asked for is how a mixer input signals completion.
        return _finished ? written * ChannelCount : framesWanted * ChannelCount;
    }

    /// <summary>
    /// End the voice because the bus it was playing on went away.
    ///
    /// A stopped bus simply stops reading its inputs, so without this the voice would sit
    /// there un-read forever: <see cref="Finished"/> would never fire, and the handle that
    /// owns it would never complete — leaving a sound stuck as "playing" that even Stop All
    /// could not clear.
    /// </summary>
    public void Abandon()
    {
        _finished = true;
        RaiseFinishedOnce();
    }

    private void RaiseFinishedOnce()
    {
        // The audio thread and Abandon() can race; only the first one through notifies.
        if (Interlocked.Exchange(ref _finishedRaised, 1) != 0) return;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private float CurrentGain()
    {
        var gain = _baseGain * ExternalGain;

        if (_fadeInFrames > 0 && _outputFrame < _fadeInFrames)
            gain *= (float)(_outputFrame / (double)_fadeInFrames);

        if (_fadeOutFrames > 0 && _totalOutputFrames > 0)
        {
            var remaining = _totalOutputFrames - _outputFrame;
            if (remaining < _fadeOutFrames)
                gain *= Math.Max(0f, remaining / (float)_fadeOutFrames);
        }

        if (_releasing)
        {
            gain *= _releaseRemaining / (float)_releaseTotal;
            if (--_releaseRemaining <= 0) _finished = true;
        }

        return gain;
    }

    /// <summary>Pull one output frame, resampling when the speed is not 1.</summary>
    private bool NextFrame(float[] destination)
    {
        if (Math.Abs(Speed - 1f) < 0.0001f)
            return ReadSourceFrame(destination);

        if (!_primed)
        {
            if (!ReadSourceFrame(_frameA)) return false;
            if (!ReadSourceFrame(_frameB)) Array.Copy(_frameA, _frameB, ChannelCount);
            _primed = true;
            _fraction = 0;
        }

        while (_fraction >= 1.0)
        {
            Array.Copy(_frameB, _frameA, ChannelCount);
            if (!ReadSourceFrame(_frameB)) return false;
            _fraction -= 1.0;
        }

        var f = (float)_fraction;
        for (var c = 0; c < ChannelCount; c++)
            destination[c] = _frameA[c] + (_frameB[c] - _frameA[c]) * f;

        _fraction += Speed;
        return true;
    }

    /// <summary>Change speed mid-playback (the properties panel slider does this live).</summary>
    public void SetSpeed(float speed)
    {
        Speed = Math.Clamp(speed, 0.25f, 4f);
        _primed = false;
        _fraction = 0;
    }

    public void SetVolume(float volume)
    {
        var gain = Math.Clamp(volume, 0f, 4f);
        if (Settings.Normalize && Settings.PeakAmplitude > 0.0001f)
            gain *= Math.Min(0.891f / Settings.PeakAmplitude, 10f);
        _baseGain = gain;
    }
}
