namespace ProjectSoundboard.Audio.Dsp;

/// <summary>
/// Feed-forward peak compressor with a soft knee. Used to even out both microphone level
/// and wildly inconsistent sound file loudness.
/// </summary>
public sealed class Compressor
{
    private float _envelopeDb;
    private float _attackCoeff;
    private float _releaseCoeff;
    private int _sampleRate = 48000;
    private float _attackMs = 10f;
    private float _releaseMs = 120f;

    public bool Enabled { get; set; }

    /// <summary>Level above which compression starts, in dBFS.</summary>
    public float ThresholdDb { get; set; } = -18f;

    /// <summary>Compression ratio, 1 = off, 20 ≈ limiting.</summary>
    public float Ratio { get; set; } = 3f;

    /// <summary>Width of the soft knee in dB.</summary>
    public float KneeDb { get; set; } = 6f;

    public float MakeupGainDb { get; set; }

    public float AttackMs
    {
        get => _attackMs;
        set { _attackMs = Math.Clamp(value, 0.1f, 500f); _attackCoeff = Coeff(_attackMs); }
    }

    public float ReleaseMs
    {
        get => _releaseMs;
        set { _releaseMs = Math.Clamp(value, 5f, 3000f); _releaseCoeff = Coeff(_releaseMs); }
    }

    public float GainReductionDb { get; private set; }

    public void Configure(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        _attackCoeff = Coeff(_attackMs);
        _releaseCoeff = Coeff(_releaseMs);
    }

    private float Coeff(float ms) =>
        (float)Math.Exp(-1.0 / (Math.Max(0.05, ms) * 0.001 * _sampleRate));

    public void Process(float[] buffer, int offset, int count, int channels)
    {
        if (!Enabled || count <= 0 || Ratio <= 1f) return;
        if (_attackCoeff == 0) Configure(_sampleRate);

        var frames = count / channels;
        var makeup = LevelMeter.FromDb(MakeupGainDb);
        var worst = 0f;

        for (var f = 0; f < frames; f++)
        {
            var baseIndex = offset + f * channels;

            var peak = 0f;
            for (var c = 0; c < channels; c++)
            {
                var abs = Math.Abs(buffer[baseIndex + c]);
                if (abs > peak) peak = abs;
            }

            var inputDb = LevelMeter.ToDb(peak);
            var over = inputDb - ThresholdDb;

            float reductionDb;
            if (over <= -KneeDb / 2)
            {
                reductionDb = 0;
            }
            else if (over >= KneeDb / 2)
            {
                reductionDb = over - over / Ratio;
            }
            else
            {
                // Quadratic soft knee across the transition region.
                var x = over + KneeDb / 2;
                reductionDb = (1 - 1 / Ratio) * x * x / (2 * KneeDb);
            }

            var coeff = reductionDb > _envelopeDb ? _attackCoeff : _releaseCoeff;
            _envelopeDb = reductionDb + (_envelopeDb - reductionDb) * coeff;

            var gain = LevelMeter.FromDb(-_envelopeDb) * makeup;
            for (var c = 0; c < channels; c++) buffer[baseIndex + c] *= gain;

            if (_envelopeDb > worst) worst = _envelopeDb;
        }

        GainReductionDb = -worst;
    }

    public void Reset()
    {
        _envelopeDb = 0;
        GainReductionDb = 0;
    }
}
