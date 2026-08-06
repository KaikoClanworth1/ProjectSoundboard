using NAudio.Wave;

namespace ProjectSoundboard.Audio.Playback;

/// <summary>
/// Fixed size lock-light ring buffer for handing samples from the capture thread to the
/// render thread. Overruns drop the oldest audio, which is the right trade for live
/// monitoring — falling behind is worse than losing a few milliseconds.
/// </summary>
public sealed class FloatRingBuffer
{
    private readonly float[] _buffer;
    private readonly Lock _gate = new();
    private int _readPos;
    private int _writePos;
    private int _count;

    public FloatRingBuffer(int capacity) => _buffer = new float[Math.Max(1024, capacity)];

    public int Capacity => _buffer.Length;

    public int Available
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>How many samples were dropped because the reader could not keep up.</summary>
    public long Overruns { get; private set; }

    /// <summary>How many samples of silence were emitted because the buffer ran dry.</summary>
    public long Underruns { get; private set; }

    public void Write(float[] source, int offset, int count)
    {
        if (count <= 0) return;

        lock (_gate)
        {
            if (count > _buffer.Length)
            {
                // Keep only the newest tail that can possibly fit.
                offset += count - _buffer.Length;
                count = _buffer.Length;
            }

            var free = _buffer.Length - _count;
            if (count > free)
            {
                var drop = count - free;
                _readPos = (_readPos + drop) % _buffer.Length;
                _count -= drop;
                Overruns += drop;
            }

            var first = Math.Min(count, _buffer.Length - _writePos);
            Array.Copy(source, offset, _buffer, _writePos, first);

            var rest = count - first;
            if (rest > 0) Array.Copy(source, offset + first, _buffer, 0, rest);

            _writePos = (_writePos + count) % _buffer.Length;
            _count += count;
        }
    }

    /// <summary>Read up to <paramref name="count"/> samples; the rest of the span is zeroed.</summary>
    public int Read(float[] destination, int offset, int count)
    {
        lock (_gate)
        {
            var take = Math.Min(count, _count);

            if (take > 0)
            {
                var first = Math.Min(take, _buffer.Length - _readPos);
                Array.Copy(_buffer, _readPos, destination, offset, first);

                var rest = take - first;
                if (rest > 0) Array.Copy(_buffer, 0, destination, offset + first, rest);

                _readPos = (_readPos + take) % _buffer.Length;
                _count -= take;
            }

            if (take < count)
            {
                Array.Clear(destination, offset + take, count - take);
                Underruns += count - take;
            }

            return take;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _readPos = _writePos = _count = 0;
        }
    }
}

/// <summary>
/// Exposes a <see cref="FloatRingBuffer"/> as a never-ending mixer input. It always
/// returns a full buffer (padding with silence) so the mixer never drops it.
/// </summary>
public sealed class RingSampleProvider : ISampleProvider
{
    private readonly FloatRingBuffer _ring;

    public RingSampleProvider(FloatRingBuffer ring, WaveFormat format)
    {
        _ring = ring;
        WaveFormat = format;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Live multiplier — the monitor path uses this for "hear myself" volume.</summary>
    public float Gain { get; set; } = 1f;

    public int Read(float[] buffer, int offset, int count)
    {
        _ring.Read(buffer, offset, count);

        if (Math.Abs(Gain - 1f) > 0.0001f)
        {
            for (var i = 0; i < count; i++) buffer[offset + i] *= Gain;
        }

        return count;
    }
}
