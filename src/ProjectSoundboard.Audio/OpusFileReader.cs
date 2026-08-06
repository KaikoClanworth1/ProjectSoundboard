using Concentus;
using Concentus.Oggfile;
using NAudio.Wave;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Audio;

/// <summary>
/// Reads Ogg-encapsulated Opus (.opus) as 32-bit float PCM.
///
/// Windows Media Foundation only understands Opus inside WebM/Matroska, and NAudio.Vorbis
/// decodes Vorbis rather than Opus, so neither of the usual readers can open a .opus file —
/// which is exactly what yt-dlp and most YouTube rippers produce. TagLib# happily reads the
/// tags and duration, so without this the files look perfectly healthy in a library and then
/// silently refuse to play.
/// </summary>
public sealed class OpusFileReader : WaveStream
{
    /// <summary>Opus always decodes at 48 kHz regardless of what went in.</summary>
    private const int OpusSampleRate = 48000;

    private readonly FileStream _file;
    private readonly OpusOggReadStream _ogg;
    private readonly int _channels;
    private readonly long _lengthBytes;

    private float[] _pending = Array.Empty<float>();
    private int _pendingOffset;
    private long _position;
    private bool _disposed;

    public OpusFileReader(string path)
    {
        _channels = ReadChannelCount(path);

        _file = File.OpenRead(path);

        try
        {
            var decoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, _channels);
            _ogg = new OpusOggReadStream(decoder, _file);

            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(OpusSampleRate, _channels);

            var seconds = _ogg.TotalTime.TotalSeconds;
            _lengthBytes = seconds > 0
                ? (long)(seconds * OpusSampleRate) * _channels * sizeof(float)
                : 0;
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public override WaveFormat WaveFormat { get; }

    public override long Length => _lengthBytes;

    public override bool CanSeek => _ogg.CanSeek;

    public override long Position
    {
        get => _position;
        set
        {
            if (!_ogg.CanSeek) return;

            var blockAlign = WaveFormat.BlockAlign;
            var target = Math.Max(0, value - value % blockAlign);
            var seconds = (double)target / (OpusSampleRate * blockAlign);

            try
            {
                _ogg.SeekTo(TimeSpan.FromSeconds(seconds));
                _position = target;
                _pending = Array.Empty<float>();
                _pendingOffset = 0;
            }
            catch (Exception ex)
            {
                Log.Debug($"Opus seek failed: {ex.Message}");
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed) return 0;

        var written = 0;

        while (written + sizeof(float) <= count)
        {
            if (_pendingOffset >= _pending.Length && !DecodeNext()) break;

            var available = _pending.Length - _pendingOffset;
            var wanted = (count - written) / sizeof(float);
            var take = Math.Min(available, wanted);

            Buffer.BlockCopy(_pending, _pendingOffset * sizeof(float),
                buffer, offset + written, take * sizeof(float));

            _pendingOffset += take;
            written += take * sizeof(float);
        }

        _position += written;
        return written;
    }

    /// <summary>Pull one Opus packet and convert it to float. False once the file runs out.</summary>
    private bool DecodeNext()
    {
        while (true)
        {
            if (!_ogg.HasNextPacket) return false;

            short[]? decoded;
            try
            {
                decoded = _ogg.DecodeNextPacket();
            }
            catch (Exception ex)
            {
                // A single corrupt page should not kill the whole track.
                Log.Debug($"Skipping bad Opus packet: {ex.Message}");
                continue;
            }

            if (decoded is null || decoded.Length == 0)
            {
                // Concentus returns null for pages it cannot use; keep going unless the
                // stream is genuinely finished.
                if (!_ogg.HasNextPacket) return false;
                continue;
            }

            if (_pending.Length < decoded.Length) _pending = new float[decoded.Length];

            for (var i = 0; i < decoded.Length; i++) _pending[i] = decoded[i] / 32768f;

            // The buffer may be longer than this packet; only expose what we decoded.
            if (_pending.Length != decoded.Length) _pending = _pending[..decoded.Length];

            _pendingOffset = 0;
            return true;
        }
    }

    /// <summary>
    /// Read the channel count out of the OpusHead identification header. Creating the
    /// decoder with the wrong channel count produces silence or garbage, and the Ogg reader
    /// does not surface it.
    /// </summary>
    private static int ReadChannelCount(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            // OpusHead lives in the first Ogg page, well inside the first few hundred bytes.
            var probe = new byte[1024];
            var read = stream.Read(probe, 0, probe.Length);

            ReadOnlySpan<byte> magic = "OpusHead"u8;

            for (var i = 0; i + magic.Length + 2 < read; i++)
            {
                if (!probe.AsSpan(i, magic.Length).SequenceEqual(magic)) continue;

                // magic(8) + version(1) then the channel count.
                var channels = probe[i + magic.Length + 1];
                if (channels is >= 1 and <= 8) return channels;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not read the Opus header for {Path.GetFileName(path)}: {ex.Message}");
        }

        // Overwhelmingly the common case for downloaded music.
        return 2;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            try { _ogg.Close(); } catch { /* already closed */ }
            _file.Dispose();
        }

        base.Dispose(disposing);
    }
}
