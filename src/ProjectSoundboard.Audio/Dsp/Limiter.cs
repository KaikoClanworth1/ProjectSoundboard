namespace ProjectSoundboard.Audio.Dsp;

/// <summary>
/// Look-free soft-knee brickwall limiter. Its job is to guarantee nothing ever leaves the
/// engine above the threshold, which is what stops the crackle and clipping people hear
/// when a soundboard is pushed into a voice chat.
/// </summary>
public sealed class Limiter
{
    private float _envelope = 1f;
    private float _attackCoeff;
    private float _releaseCoeff;
    private int _sampleRate = 48000;
    private float _thresholdDb = -1f;
    private float _thresholdLinear = 0.891f;

    public bool Enabled { get; set; } = true;

    public float ThresholdDb
    {
        get => _thresholdDb;
        set
        {
            _thresholdDb = Math.Clamp(value, -30f, 0f);
            _thresholdLinear = LevelMeter.FromDb(_thresholdDb);
        }
    }

    /// <summary>Gain reduction currently applied, in dB (negative). For the UI meter.</summary>
    public float GainReductionDb { get; private set; }

    public void Configure(int sampleRate, float attackMs = 1.5f, float releaseMs = 80f)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        _attackCoeff = Coeff(attackMs);
        _releaseCoeff = Coeff(releaseMs);
    }

    private float Coeff(float ms) =>
        (float)Math.Exp(-1.0 / (Math.Max(0.05, ms) * 0.001 * _sampleRate));

    public void Process(float[] buffer, int offset, int count, int channels)
    {
        if (!Enabled || count <= 0) return;
        if (_attackCoeff == 0) Configure(_sampleRate);

        var frames = count / channels;
        var maxReduction = 0f;

        for (var f = 0; f < frames; f++)
        {
            var baseIndex = offset + f * channels;

            // Link the channels so the stereo image never shifts under limiting.
            var peak = 0f;
            for (var c = 0; c < channels; c++)
            {
                var abs = Math.Abs(buffer[baseIndex + c]);
                if (abs > peak) peak = abs;
            }

            var target = peak > _thresholdLinear ? _thresholdLinear / peak : 1f;

            // Clamp down fast, let go slowly.
            var coeff = target < _envelope ? _attackCoeff : _releaseCoeff;
            _envelope = target + (_envelope - target) * coeff;

            for (var c = 0; c < channels; c++)
            {
                var v = buffer[baseIndex + c] * _envelope;
                // Final hard safety clamp — the envelope can lag on a sharp transient.
                buffer[baseIndex + c] = Math.Clamp(v, -_thresholdLinear, _thresholdLinear);
            }

            if (_envelope < 1f)
            {
                var reduction = 1f - _envelope;
                if (reduction > maxReduction) maxReduction = reduction;
            }
        }

        GainReductionDb = maxReduction <= 0 ? 0 : LevelMeter.ToDb(1f - maxReduction);
    }

    public void Reset()
    {
        _envelope = 1f;
        GainReductionDb = 0;
    }
}
