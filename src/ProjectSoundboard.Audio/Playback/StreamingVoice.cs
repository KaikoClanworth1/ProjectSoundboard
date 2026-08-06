using NAudio.Wave;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Audio.Playback;

/// <summary>
/// A voice that decodes from disk while it plays. Used for long files (music beds, full
/// tracks) that would be wasteful to hold in memory.
/// </summary>
public sealed class StreamingVoice : VoiceBase, IDisposable
{
    private readonly AudioSource _source;
    private readonly float[] _buffer;
    private readonly long _startFrame;
    private readonly long _endFrame;

    private int _bufferFrames;
    private int _bufferPosition;
    private long _frame;
    private bool _disposed;

    public StreamingVoice(string path, VoiceSettings settings, WaveFormat format)
        : base(format, settings)
    {
        _source = AudioFileFactory.Open(path, format.SampleRate, format.Channels);

        // ~85 ms of audio per refill: big enough that decoding stays off the critical
        // path, small enough that a stop feels immediate.
        _buffer = new float[4096 * format.Channels];

        _startFrame = Math.Max(0, MsToFrames(settings.TrimStartMs));

        var total = (long)(_source.Duration.TotalSeconds * format.SampleRate);
        var end = settings.TrimEndMs > 0 ? MsToFrames(settings.TrimEndMs) : total;
        _endFrame = end > _startFrame ? end : long.MaxValue;

        if (_startFrame > 0) SeekStream(_startFrame);
        _frame = _startFrame;

        if (total > 0) SetTotalOutputFrames(Math.Min(_endFrame, total) - _startFrame);
    }

    public override double PositionSeconds => (double)_frame / SampleRate;

    public override double LengthSeconds
    {
        get
        {
            var total = _source.Duration.TotalSeconds;
            var end = _endFrame == long.MaxValue ? total : _endFrame / (double)SampleRate;
            return Math.Max(0, end - _startFrame / (double)SampleRate);
        }
    }

    protected override void SeekToStart()
    {
        SeekStream(_startFrame);
        _frame = _startFrame;
        _bufferFrames = 0;
        _bufferPosition = 0;
    }

    private void SeekStream(long frame)
    {
        try
        {
            if (!_source.Stream.CanSeek) return;
            var seconds = frame / (double)SampleRate;
            _source.Stream.CurrentTime = TimeSpan.FromSeconds(
                Math.Clamp(seconds, 0, _source.Duration.TotalSeconds));
        }
        catch (Exception ex)
        {
            Log.Debug($"Seek failed: {ex.Message}");
        }
    }

    protected override bool ReadSourceFrame(float[] destination)
    {
        if (_disposed) return false;

        if (_frame >= _endFrame)
        {
            if (!Settings.Loop) return false;
            SeekToStart();
        }

        if (_bufferPosition >= _bufferFrames)
        {
            int read;
            try { read = _source.Provider.Read(_buffer, 0, _buffer.Length); }
            catch (Exception ex)
            {
                Log.Warn($"Streaming read failed: {ex.Message}");
                return false;
            }

            if (read <= 0)
            {
                if (!Settings.Loop) return false;

                SeekToStart();
                try { read = _source.Provider.Read(_buffer, 0, _buffer.Length); }
                catch { return false; }
                if (read <= 0) return false;
            }

            _bufferFrames = read / ChannelCount;
            _bufferPosition = 0;
            if (_bufferFrames == 0) return false;
        }

        var offset = _bufferPosition * ChannelCount;
        for (var c = 0; c < ChannelCount; c++)
            destination[c] = _buffer[offset + c];

        _bufferPosition++;
        _frame++;
        return true;
    }

    public void Seek(double seconds)
    {
        SeekStream((long)(seconds * SampleRate));
        _frame = (long)(seconds * SampleRate);
        _bufferFrames = 0;
        _bufferPosition = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.Dispose();
    }
}
