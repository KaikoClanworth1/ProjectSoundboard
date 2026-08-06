using NAudio.Wave;

namespace ProjectSoundboard.Audio.Playback;

/// <summary>
/// A voice that plays from a <see cref="CachedSound"/> already in memory. Starting one is
/// allocation-light and involves no disk access, which is what keeps trigger latency down.
/// </summary>
public sealed class CachedVoice : VoiceBase
{
    private readonly CachedSound _sound;
    private readonly long _startFrame;
    private readonly long _endFrame;

    private long _frame;

    public CachedVoice(CachedSound sound, VoiceSettings settings, WaveFormat format)
        : base(format, settings)
    {
        _sound = sound;

        _startFrame = Math.Clamp(MsToFrames(settings.TrimStartMs), 0, sound.Frames);

        var end = settings.TrimEndMs > 0 ? MsToFrames(settings.TrimEndMs) : sound.Frames;
        _endFrame = Math.Clamp(end, _startFrame, sound.Frames);

        _frame = _startFrame;

        SetTotalOutputFrames(_endFrame - _startFrame);
    }

    public override double PositionSeconds => (double)_frame / _sound.SampleRate;

    public override double LengthSeconds => (double)(_endFrame - _startFrame) / _sound.SampleRate;

    protected override void SeekToStart() => _frame = _startFrame;

    protected override bool ReadSourceFrame(float[] destination)
    {
        if (_frame >= _endFrame)
        {
            if (!Settings.Loop) return false;
            _frame = _startFrame;
            if (_frame >= _endFrame) return false; // zero length region — refuse to spin
        }

        var offset = (int)(_frame * ChannelCount);
        var data = _sound.Data;

        for (var c = 0; c < ChannelCount; c++)
            destination[c] = data[offset + c];

        _frame++;
        return true;
    }

    /// <summary>Jump to an absolute position in the file (used by the waveform scrubber).</summary>
    public void Seek(double seconds)
    {
        var frame = (long)(seconds * _sound.SampleRate);
        _frame = Math.Clamp(frame, _startFrame, _endFrame);
    }
}
