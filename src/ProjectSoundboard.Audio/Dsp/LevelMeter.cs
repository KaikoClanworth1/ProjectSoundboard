namespace ProjectSoundboard.Audio.Dsp;

/// <summary>
/// Peak and RMS meter with a fast attack / slow decay ballistic, so the UI reads like a
/// hardware meter instead of flickering. Written from the audio thread, read from the UI
/// thread — floats are read/written atomically so no lock is needed.
/// </summary>
public sealed class LevelMeter
{
    private float _peak;
    private float _rms;
    private float _peakHold;
    private int _holdCounter;

    /// <summary>How quickly the meter falls back, per processed block (0..1).</summary>
    public float Decay { get; set; } = 0.16f;

    /// <summary>Blocks the peak-hold marker stays put before falling.</summary>
    public int HoldBlocks { get; set; } = 24;

    /// <summary>Instantaneous peak, 0..1+.</summary>
    public float Peak => _peak;

    /// <summary>Smoothed RMS, 0..1.</summary>
    public float Rms => _rms;

    /// <summary>Highest peak seen recently, for the "hold" tick.</summary>
    public float PeakHold => _peakHold;

    public float PeakDb => ToDb(_peak);
    public float RmsDb => ToDb(_rms);

    /// <summary>True when a block clipped, latched until <see cref="ResetClip"/>.</summary>
    public bool Clipped { get; private set; }

    public void Process(float[] buffer, int offset, int count)
    {
        if (count <= 0) return;

        var peak = 0f;
        var sum = 0.0;

        for (var i = 0; i < count; i++)
        {
            var v = buffer[offset + i];
            var abs = Math.Abs(v);
            if (abs > peak) peak = abs;
            sum += (double)v * v;
        }

        if (peak >= 0.999f) Clipped = true;

        // Attack instantly, release gradually.
        _peak = peak > _peak ? peak : _peak + (peak - _peak) * Decay;

        var rms = (float)Math.Sqrt(sum / count);
        _rms = rms > _rms ? rms : _rms + (rms - _rms) * Decay;

        if (peak >= _peakHold)
        {
            _peakHold = peak;
            _holdCounter = HoldBlocks;
        }
        else if (--_holdCounter <= 0)
        {
            _peakHold += (_peak - _peakHold) * Decay;
        }
    }

    public void ResetClip() => Clipped = false;

    public void Reset()
    {
        _peak = _rms = _peakHold = 0;
        Clipped = false;
    }

    public static float ToDb(float linear) =>
        linear <= 0.0000001f ? -96f : (float)(20 * Math.Log10(linear));

    public static float FromDb(float db) => (float)Math.Pow(10, db / 20.0);
}
