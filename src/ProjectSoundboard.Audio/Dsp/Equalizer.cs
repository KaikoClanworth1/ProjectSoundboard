using NAudio.Dsp;

namespace ProjectSoundboard.Audio.Dsp;

/// <summary>
/// Fixed five band peaking EQ (80 Hz / 300 Hz / 1 kHz / 4 kHz / 12 kHz). One biquad per
/// band per channel so stereo material stays phase coherent.
/// </summary>
public sealed class Equalizer
{
    public static readonly float[] Frequencies = { 80f, 300f, 1000f, 4000f, 12000f };
    public static readonly string[] BandNames = { "80 Hz", "300 Hz", "1 kHz", "4 kHz", "12 kHz" };

    private const float Q = 0.9f;

    private BiQuadFilter[,]? _filters; // [channel, band]
    private readonly float[] _gainsDb = new float[Frequencies.Length];
    private int _sampleRate;
    private int _channels;

    public bool Enabled { get; set; }

    public int BandCount => Frequencies.Length;

    public float GetBandDb(int band) => _gainsDb[band];

    public void SetBandDb(int band, float db)
    {
        if (band < 0 || band >= _gainsDb.Length) return;
        db = Math.Clamp(db, -18f, 18f);
        if (Math.Abs(_gainsDb[band] - db) < 0.001f) return;

        _gainsDb[band] = db;
        UpdateBand(band);
    }

    public void SetBands(IReadOnlyList<float> gainsDb)
    {
        for (var i = 0; i < Math.Min(gainsDb.Count, _gainsDb.Length); i++)
            SetBandDb(i, gainsDb[i]);
    }

    public void Configure(int sampleRate, int channels)
    {
        if (_sampleRate == sampleRate && _channels == channels && _filters is not null) return;

        _sampleRate = sampleRate;
        _channels = channels;
        _filters = new BiQuadFilter[channels, Frequencies.Length];

        for (var c = 0; c < channels; c++)
        {
            for (var b = 0; b < Frequencies.Length; b++)
            {
                _filters[c, b] = BiQuadFilter.PeakingEQ(sampleRate, Frequencies[b], Q, _gainsDb[b]);
            }
        }
    }

    private void UpdateBand(int band)
    {
        if (_filters is null) return;
        for (var c = 0; c < _channels; c++)
            _filters[c, band].SetPeakingEq(_sampleRate, Frequencies[band], Q, _gainsDb[band]);
    }

    public void Process(float[] buffer, int offset, int count, int channels)
    {
        if (!Enabled || _filters is null || count <= 0) return;
        if (channels != _channels) Configure(_sampleRate, channels);

        // Skip bands sitting at unity — an unused EQ should cost nothing.
        Span<int> active = stackalloc int[Frequencies.Length];
        var activeCount = 0;
        for (var b = 0; b < Frequencies.Length; b++)
        {
            if (Math.Abs(_gainsDb[b]) > 0.05f) active[activeCount++] = b;
        }
        if (activeCount == 0) return;

        var frames = count / channels;
        for (var f = 0; f < frames; f++)
        {
            var baseIndex = offset + f * channels;
            for (var c = 0; c < channels; c++)
            {
                var v = buffer[baseIndex + c];
                for (var i = 0; i < activeCount; i++)
                    v = _filters![c, active[i]].Transform(v);
                buffer[baseIndex + c] = v;
            }
        }
    }

    public void Reset() => _filters = null;
}
