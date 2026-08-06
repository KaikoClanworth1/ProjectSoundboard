using NAudio.Wave;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Audio.Playback;

/// <summary>
/// A voice that decodes from disk while it plays. Used for long files (music beds, full
/// tracks) that would be wasteful to hold in memory.
///
/// Decoding happens on a dedicated background thread that keeps a ring buffer topped up;
/// the audio thread only ever memcpys out of that ring. Decoding MP3 or Opus and resampling
/// it directly on the WASAPI render thread works fine on a fast machine and misses the
/// buffer deadline on a slow one — which is heard as stuttering, robotic playback.
/// </summary>
public sealed class StreamingVoice : VoiceBase, IDisposable
{
    /// <summary>How much decoded audio to keep ahead of the render thread.</summary>
    private const double BufferSeconds = 1.5;

    /// <summary>Decoded up front so playback never starts on an empty ring.</summary>
    private const double PrimeSeconds = 0.25;

    private readonly AudioSource _source;
    private readonly FloatRingBuffer _ring;
    private readonly float[] _block;        // render-side staging, pulled from the ring
    private readonly float[] _decodeBuffer; // decoder-side staging
    private readonly long _startFrame;
    private readonly long _endFrame;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Thread _decoder;

    private int _blockFrames;
    private int _blockPosition;

    /// <summary>Position in the file, owned by the decoder thread.</summary>
    private long _sourceFrame;

    /// <summary>Seek target for the decoder, or -1. Set by the audio thread.</summary>
    private long _seekRequest = -1;

    private volatile bool _decoderEnded;
    private volatile bool _disposed;

    /// <summary>Frames handed to the mixer, for position reporting.</summary>
    private long _renderFrame;

    private int _starvations;

    /// <summary>
    /// How many times the audio thread reached the ring buffer and found it empty while the
    /// decoder was still working — in other words, how often the machine failed to keep up.
    /// Should be zero during steady playback; a non-zero count is what "robotic" sounds like.
    /// </summary>
    public int Starvations => Volatile.Read(ref _starvations);

    public StreamingVoice(string path, VoiceSettings settings, WaveFormat format)
        : base(format, settings)
    {
        _source = AudioFileFactory.Open(path, format.SampleRate, format.Channels);

        var channels = format.Channels;
        _ring = new FloatRingBuffer((int)(BufferSeconds * format.SampleRate) * channels);
        _block = new float[4096 * channels];
        _decodeBuffer = new float[8192 * channels];

        _startFrame = Math.Max(0, MsToFrames(settings.TrimStartMs));

        var total = (long)(_source.Duration.TotalSeconds * format.SampleRate);
        var end = settings.TrimEndMs > 0 ? MsToFrames(settings.TrimEndMs) : total;
        _endFrame = end > _startFrame ? end : long.MaxValue;

        if (_startFrame > 0) SeekStream(_startFrame);
        _sourceFrame = _startFrame;
        _renderFrame = _startFrame;

        if (total > 0) SetTotalOutputFrames(Math.Min(_endFrame, total) - _startFrame);

        // Fill enough to cover the first few buffer callbacks before handing over.
        Decode((int)(PrimeSeconds * format.SampleRate) * channels);

        _decoder = new Thread(DecodeLoop)
        {
            IsBackground = true,
            Name = "sound-decoder",
            // Above normal so decoding is not starved by UI work, but below the audio
            // thread, which WASAPI already runs at a raised priority.
            Priority = ThreadPriority.AboveNormal
        };
        _decoder.Start();
    }

    public override double PositionSeconds => (double)_renderFrame / SampleRate;

    public override double LengthSeconds
    {
        get
        {
            var total = _source.Duration.TotalSeconds;
            var end = _endFrame == long.MaxValue ? total : _endFrame / (double)SampleRate;
            return Math.Max(0, end - _startFrame / (double)SampleRate);
        }
    }

    // -----------------------------------------------------------------------
    // Decoder thread
    // -----------------------------------------------------------------------

    private void DecodeLoop()
    {
        try
        {
            while (!_cancel.IsCancellationRequested && !_disposed)
            {
                var free = _ring.Capacity - _ring.Available;

                // Keep it comfortably full without spinning.
                if (free < _decodeBuffer.Length)
                {
                    Thread.Sleep(5);
                    continue;
                }

                if (!Decode(_decodeBuffer.Length))
                {
                    // Nothing more to produce; idle until disposed so the ring can drain.
                    Thread.Sleep(20);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Decoder thread stopped: {ex.Message}");
            _decoderEnded = true;
        }
    }

    /// <summary>
    /// Decode up to <paramref name="maxSamples"/> into the ring. Returns false when the
    /// source is exhausted. Runs on the decoder thread, and once from the constructor
    /// before that thread exists.
    /// </summary>
    private bool Decode(int maxSamples)
    {
        var pending = Interlocked.Exchange(ref _seekRequest, -1);
        if (pending >= 0)
        {
            var target = Math.Clamp(pending, _startFrame,
                _endFrame == long.MaxValue ? long.MaxValue : _endFrame);

            SeekStream(target);
            _sourceFrame = target;
            _decoderEnded = false;
            _ring.Clear();
        }

        if (_decoderEnded) return false;

        // Stop at the trim point.
        var remaining = _endFrame == long.MaxValue ? long.MaxValue : _endFrame - _sourceFrame;
        if (remaining <= 0)
        {
            if (!Settings.Loop) { _decoderEnded = true; return false; }
            SeekStream(_startFrame);
            _sourceFrame = _startFrame;
            remaining = _endFrame - _startFrame;
        }

        var wanted = (int)Math.Min(maxSamples, remaining * ChannelCount);
        wanted = Math.Min(wanted, _decodeBuffer.Length);
        if (wanted <= 0) return false;

        int read;
        try
        {
            read = _source.Provider.Read(_decodeBuffer, 0, wanted);
        }
        catch (Exception ex)
        {
            Log.Warn($"Streaming decode failed: {ex.Message}");
            _decoderEnded = true;
            return false;
        }

        if (read <= 0)
        {
            if (!Settings.Loop) { _decoderEnded = true; return false; }

            SeekStream(_startFrame);
            _sourceFrame = _startFrame;
            return true;
        }

        _ring.Write(_decodeBuffer, 0, read);
        _sourceFrame += read / ChannelCount;
        return true;
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

    // -----------------------------------------------------------------------
    // Audio thread
    // -----------------------------------------------------------------------

    protected override void SeekToStart() => RequestSeek(_startFrame / (double)SampleRate);

    protected override void ApplySeek(long frame)
    {
        // Hand the work to the decoder — only it may touch the stream. Dropping what we
        // already have avoids a moment of audio from the old position.
        Interlocked.Exchange(ref _seekRequest, frame);
        _ring.Clear();
        _blockFrames = 0;
        _blockPosition = 0;
        _renderFrame = frame;
    }

    protected override bool ReadSourceFrame(float[] destination)
    {
        if (_disposed) return false;

        if (_blockPosition >= _blockFrames)
        {
            var read = _ring.Read(_block, 0, _block.Length);

            if (read == 0)
            {
                // Genuinely finished, or the decoder has not caught up yet. Ending the
                // voice on a momentary shortfall would truncate the sound, so only stop
                // when the decoder says there is nothing left.
                if (_decoderEnded) return false;

                // Emit a little silence and let the decoder catch up.
                Interlocked.Increment(ref _starvations);
                Array.Clear(_block);
                _blockFrames = Math.Min(64, _block.Length / ChannelCount);
            }
            else
            {
                _blockFrames = read / ChannelCount;
            }

            _blockPosition = 0;
            if (_blockFrames == 0) return false;
        }

        var offset = _blockPosition * ChannelCount;
        for (var c = 0; c < ChannelCount; c++) destination[c] = _block[offset + c];

        _blockPosition++;
        _renderFrame++;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cancel.Cancel();

        // The decoder owns the stream, so it has to be finished with it before we close it.
        if (_decoder.IsAlive && !_decoder.Join(TimeSpan.FromSeconds(2)))
            Log.Debug("Decoder thread did not stop in time.");

        _cancel.Dispose();
        _source.Dispose();
    }
}
