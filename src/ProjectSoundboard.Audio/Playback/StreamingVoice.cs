using NAudio.Wave;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Audio.Playback;

/// <summary>
/// A voice that decodes from disk while it plays. Used for long files (music beds, full
/// tracks) that would be wasteful to hold in memory.
///
/// Decoding happens on a dedicated background thread that keeps a ring buffer topped up;
/// the audio thread only ever memcpys out of that ring. Decoding directly on the WASAPI
/// render thread works on a fast machine and misses the buffer deadline on a slow one,
/// which is heard as stuttering, robotic playback.
///
/// The decoder is also *opened* on that thread. Media Foundation readers are COM objects
/// with apartment affinity: created on the UI thread and then read from a background one,
/// every read fails with E_NOINTERFACE and the sound stops a fraction of a second after it
/// starts. Everything that touches the reader now lives on the one thread.
/// </summary>
public sealed class StreamingVoice : VoiceBase, IDisposable
{
    /// <summary>How much decoded audio to keep ahead of the render thread.</summary>
    private const double BufferSeconds = 1.5;

    /// <summary>Decoded before playback starts, so the ring is never empty at the start.</summary>
    private const double PrimeSeconds = 0.25;

    private readonly string _path;
    private readonly FloatRingBuffer _ring;
    private readonly float[] _block;        // render-side staging, pulled from the ring
    private readonly float[] _decodeBuffer; // decoder-side staging
    private readonly CancellationTokenSource _cancel = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _decoder;

    // Owned by the decoder thread once it starts.
    private AudioSource? _source;
    private Exception? _openFailure;
    private double _durationSeconds;
    private long _startFrame;
    private long _endFrame = long.MaxValue;

    private int _blockFrames;
    private int _blockPosition;
    private long _sourceFrame;
    private long _seekRequest = -1;

    private volatile bool _decoderEnded;
    private volatile bool _disposed;

    private long _renderFrame;
    private int _starvations;

    public StreamingVoice(string path, VoiceSettings settings, WaveFormat format)
        : base(format, settings)
    {
        _path = path;

        var channels = format.Channels;
        _ring = new FloatRingBuffer((int)(BufferSeconds * format.SampleRate) * channels);
        _block = new float[4096 * channels];
        _decodeBuffer = new float[8192 * channels];

        _decoder = new Thread(DecodeLoop)
        {
            IsBackground = true,
            Name = "sound-decoder",
            // Above normal so decoding is not starved by UI work, but below the audio
            // thread, which WASAPI already runs at a raised priority.
            Priority = ThreadPriority.AboveNormal
        };

        // Media Foundation is happiest in the multi-threaded apartment, and this keeps the
        // reader away from the UI thread's STA entirely.
        if (OperatingSystem.IsWindows()) _decoder.SetApartmentState(ApartmentState.MTA);
        _decoder.Start();

        // Wait for the file to open so a failure surfaces as a normal "cannot play this"
        // rather than a sound that starts and immediately stops.
        if (!_ready.Wait(TimeSpan.FromSeconds(15)))
        {
            Dispose();
            throw new TimeoutException($"Timed out opening '{Path.GetFileName(path)}'.");
        }

        if (_openFailure is not null)
        {
            Dispose();
            throw _openFailure;
        }
    }

    public override double PositionSeconds => (double)Volatile.Read(ref _renderFrame) / SampleRate;

    public override double LengthSeconds
    {
        get
        {
            var end = _endFrame == long.MaxValue ? _durationSeconds : _endFrame / (double)SampleRate;
            return Math.Max(0, end - _startFrame / (double)SampleRate);
        }
    }

    /// <summary>
    /// How many times the audio thread found the ring empty while the decoder was still
    /// working — how often this machine failed to keep up. Zero during steady playback.
    /// </summary>
    public int Starvations => Volatile.Read(ref _starvations);

    // -----------------------------------------------------------------------
    // Decoder thread — the only thread that may touch _source
    // -----------------------------------------------------------------------

    private void DecodeLoop()
    {
        try
        {
            _source = AudioFileFactory.Open(_path, SampleRate, ChannelCount);
            _durationSeconds = _source.Duration.TotalSeconds;

            _startFrame = Math.Max(0, MsToFrames(Settings.TrimStartMs));

            var total = (long)(_durationSeconds * SampleRate);
            var end = Settings.TrimEndMs > 0 ? MsToFrames(Settings.TrimEndMs) : total;
            _endFrame = end > _startFrame ? end : long.MaxValue;

            if (_startFrame > 0) SeekStream(_startFrame);
            _sourceFrame = _startFrame;
            Volatile.Write(ref _renderFrame, _startFrame);

            if (total > 0) SetTotalOutputFrames(Math.Min(_endFrame, total) - _startFrame);

            Decode((int)(PrimeSeconds * SampleRate) * ChannelCount);
        }
        catch (Exception ex)
        {
            _openFailure = ex;
            _decoderEnded = true;
        }
        finally
        {
            _ready.Set();
        }

        if (_openFailure is not null) return;

        try
        {
            while (!_cancel.IsCancellationRequested && !_disposed)
            {
                if (_ring.Capacity - _ring.Available < _decodeBuffer.Length)
                {
                    Thread.Sleep(5);
                    continue;
                }

                // Nothing more to produce; idle so the ring can drain.
                if (!Decode(_decodeBuffer.Length)) Thread.Sleep(20);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Decoder thread stopped: {ex.Message}");
            _decoderEnded = true;
        }
    }

    /// <summary>Decode into the ring. Returns false once the source is exhausted.</summary>
    private bool Decode(int maxSamples)
    {
        if (_source is null) return false;

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

        var remaining = _endFrame == long.MaxValue ? long.MaxValue : _endFrame - _sourceFrame;
        if (remaining <= 0)
        {
            if (!Settings.Loop) { _decoderEnded = true; return false; }

            SeekStream(_startFrame);
            _sourceFrame = _startFrame;
            remaining = _endFrame - _startFrame;
        }

        var wanted = (int)Math.Min(Math.Min(maxSamples, remaining * ChannelCount), _decodeBuffer.Length);
        if (wanted <= 0) return false;

        int read;
        try
        {
            read = _source.Provider.Read(_decodeBuffer, 0, wanted);
        }
        catch (Exception ex)
        {
            Log.Warn($"Streaming decode failed for '{Path.GetFileName(_path)}': {ex.Message}");
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
        if (_source is null) return;

        try
        {
            if (!_source.Stream.CanSeek) return;

            var seconds = frame / (double)SampleRate;
            _source.Stream.CurrentTime = TimeSpan.FromSeconds(
                Math.Clamp(seconds, 0, _durationSeconds));
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
        // Only the decoder may touch the stream, so hand it the request and drop what we
        // already hold rather than playing a moment from the old position.
        Interlocked.Exchange(ref _seekRequest, frame);
        _ring.Clear();
        _blockFrames = 0;
        _blockPosition = 0;
        Volatile.Write(ref _renderFrame, frame);
    }

    protected override bool ReadSourceFrame(float[] destination)
    {
        if (_disposed) return false;

        if (_blockPosition >= _blockFrames)
        {
            var read = _ring.Read(_block, 0, _block.Length);

            if (read == 0)
            {
                // Genuinely finished, or the decoder has not caught up. Ending the voice on
                // a momentary shortfall would truncate the sound.
                if (_decoderEnded) return false;

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
        Interlocked.Increment(ref _renderFrame);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cancel.Cancel();

        // The decoder owns the reader, so it must be done before we close it.
        if (_decoder.IsAlive && !_decoder.Join(TimeSpan.FromSeconds(2)))
            Log.Debug("Decoder thread did not stop in time.");

        _cancel.Dispose();
        _ready.Dispose();
        _source?.Dispose();
    }
}
