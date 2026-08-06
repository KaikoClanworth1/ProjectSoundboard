namespace ProjectSoundboard.Audio.Dsp;

/// <summary>
/// Lightweight broadband noise suppressor: it tracks the quietest level it has seen
/// recently as a noise floor estimate, then applies downward expansion to anything close
/// to it. This is not a spectral subtraction / RNNoise style suppressor — it removes
/// steady hiss and fan noise well, and deliberately does nothing clever with speech.
/// </summary>
public sealed class NoiseSuppressor
{
    private float _noiseFloor = 0.001f;
    private float _envelope;
    private float _gain = 1f;
    private int _sampleRate = 48000;
    private float _smoothCoeff;

    public bool Enabled { get; set; }

    /// <summary>0 = off, 1 = maximum expansion below the estimated floor.</summary>
    public float Strength { get; set; } = 0.7f;

    /// <summary>How far above the noise floor the signal must sit to pass untouched (dB).</summary>
    public float MarginDb { get; set; } = 9f;

    public float EstimatedNoiseFloorDb => LevelMeter.ToDb(_noiseFloor);

    public void Configure(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        // ~15 ms envelope smoothing.
        _smoothCoeff = (float)Math.Exp(-1.0 / (0.015 * _sampleRate));
    }

    public void Process(float[] buffer, int offset, int count, int channels)
    {
        if (!Enabled || count <= 0) return;
        if (_smoothCoeff == 0) Configure(_sampleRate);

        var frames = count / channels;
        var margin = LevelMeter.FromDb(MarginDb);
        var strength = Math.Clamp(Strength, 0f, 1f);

        for (var f = 0; f < frames; f++)
        {
            var baseIndex = offset + f * channels;

            var peak = 0f;
            for (var c = 0; c < channels; c++)
            {
                var abs = Math.Abs(buffer[baseIndex + c]);
                if (abs > peak) peak = abs;
            }

            _envelope = peak + (_envelope - peak) * _smoothCoeff;

            // Track the floor downward fast and upward very slowly, so a long silence
            // re-learns the room but a sustained note never gets treated as noise.
            if (_envelope < _noiseFloor) _noiseFloor += (_envelope - _noiseFloor) * 0.02f;
            else _noiseFloor += (_envelope - _noiseFloor) * 0.00002f;

            _noiseFloor = Math.Clamp(_noiseFloor, 0.000005f, 0.2f);

            var threshold = _noiseFloor * margin;
            float target;

            if (_envelope >= threshold)
            {
                target = 1f;
            }
            else
            {
                var ratio = threshold <= 0 ? 1f : _envelope / threshold;
                target = 1f - strength * (1f - ratio * ratio);
            }

            _gain += (target - _gain) * 0.02f;
            for (var c = 0; c < channels; c++) buffer[baseIndex + c] *= _gain;
        }
    }

    public void Reset()
    {
        _noiseFloor = 0.001f;
        _envelope = 0;
        _gain = 1f;
    }
}
