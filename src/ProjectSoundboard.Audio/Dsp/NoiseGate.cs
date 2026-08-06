namespace ProjectSoundboard.Audio.Dsp;

/// <summary>
/// Classic attack / hold / release noise gate. Keeps keyboard clatter and fan noise out of
/// the microphone passthrough without chopping the front off words.
/// </summary>
public sealed class NoiseGate
{
    private enum State { Closed, Attacking, Open, Holding, Releasing }

    private State _state = State.Closed;
    private float _gain;
    private int _holdSamples;
    private int _holdCounter;
    private float _attackStep = 1f;
    private float _releaseStep = 1f;
    private int _sampleRate = 48000;

    public bool Enabled { get; set; } = true;

    /// <summary>Level the signal must exceed for the gate to open, in dBFS.</summary>
    public float ThresholdDb { get; set; } = -45f;

    /// <summary>How far below the threshold the signal must drop before closing (dB).</summary>
    public float HysteresisDb { get; set; } = 4f;

    public float AttackMs { get; set; } = 5f;
    public float HoldMs { get; set; } = 120f;
    public float ReleaseMs { get; set; } = 180f;

    /// <summary>True while audio is passing — drives the "talking" indicator in the UI.</summary>
    public bool IsOpen => _state is State.Open or State.Attacking or State.Holding;

    public void Configure(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        _attackStep = 1f / Math.Max(1, (int)(AttackMs * 0.001f * _sampleRate));
        _releaseStep = 1f / Math.Max(1, (int)(ReleaseMs * 0.001f * _sampleRate));
        _holdSamples = Math.Max(1, (int)(HoldMs * 0.001f * _sampleRate));
    }

    public void Process(float[] buffer, int offset, int count, int channels)
    {
        if (!Enabled)
        {
            _gain = 1f;
            _state = State.Open;
            return;
        }

        if (_attackStep >= 1f) Configure(_sampleRate);

        var openLevel = LevelMeter.FromDb(ThresholdDb);
        var closeLevel = LevelMeter.FromDb(ThresholdDb - HysteresisDb);
        var frames = count / channels;

        for (var f = 0; f < frames; f++)
        {
            var baseIndex = offset + f * channels;

            var peak = 0f;
            for (var c = 0; c < channels; c++)
            {
                var abs = Math.Abs(buffer[baseIndex + c]);
                if (abs > peak) peak = abs;
            }

            switch (_state)
            {
                case State.Closed:
                    if (peak > openLevel) _state = State.Attacking;
                    break;

                case State.Attacking:
                    _gain += _attackStep;
                    if (_gain >= 1f) { _gain = 1f; _state = State.Open; }
                    break;

                case State.Open:
                    if (peak < closeLevel) { _state = State.Holding; _holdCounter = _holdSamples; }
                    break;

                case State.Holding:
                    if (peak > openLevel) _state = State.Open;
                    else if (--_holdCounter <= 0) _state = State.Releasing;
                    break;

                case State.Releasing:
                    if (peak > openLevel) { _state = State.Attacking; break; }
                    _gain -= _releaseStep;
                    if (_gain <= 0f) { _gain = 0f; _state = State.Closed; }
                    break;
            }

            for (var c = 0; c < channels; c++) buffer[baseIndex + c] *= _gain;
        }
    }

    public void Reset()
    {
        _state = State.Closed;
        _gain = 0;
        _holdCounter = 0;
    }
}
