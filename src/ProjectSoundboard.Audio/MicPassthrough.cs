using NAudio.CoreAudioApi;
using NAudio.Wave;
using ProjectSoundboard.Audio.Dsp;
using ProjectSoundboard.Audio.Playback;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Audio;

/// <summary>
/// Captures the real microphone, runs it through the processing chain and mixes it into
/// the virtual cable. That is what lets one device carry both the user's voice and the
/// soundboard, so Discord and VRChat only ever need a single microphone selected.
/// </summary>
public sealed class MicPassthrough : IDisposable
{
    private readonly SettingsService _settings;
    private readonly AudioDeviceService _devices;
    private readonly AudioEngine _engine;
    private readonly Lock _gate = new();

    private WasapiCapture? _capture;
    private FloatRingBuffer? _virtualRing;
    private FloatRingBuffer? _monitorRing;
    private RingSampleProvider? _virtualProvider;
    private RingSampleProvider? _monitorProvider;

    private WaveFormat? _engineFormat;
    private int _captureChannels;
    private int _captureRate;
    private bool _captureIsFloat;
    private int _captureBits;

    // Resampler state (linear interpolation between capture and engine rates).
    private double _resamplePosition;
    private float[] _previousFrame = Array.Empty<float>();
    private bool _hasPreviousFrame;

    private float[] _work = Array.Empty<float>();
    private bool _disposed;

    public MicPassthrough(SettingsService settings, AudioDeviceService devices, AudioEngine engine)
    {
        _settings = settings;
        _devices = devices;
        _engine = engine;
    }

    private static bool IsVirtualCable(string? deviceName) =>
        deviceName is not null &&
        (deviceName.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
         || deviceName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
         || deviceName.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase)
         || deviceName.Contains("Virtual Audio", StringComparison.OrdinalIgnoreCase));

    public NoiseGate Gate { get; } = new();
    public Compressor Compressor { get; } = new();
    public Limiter Limiter { get; } = new();
    public NoiseSuppressor NoiseSuppressor { get; } = new();

    /// <summary>Level before processing — what the microphone actually sends.</summary>
    public LevelMeter InputMeter { get; } = new();

    /// <summary>Level after processing — what the voice app will receive.</summary>
    public LevelMeter OutputMeter { get; } = new();

    public bool IsRunning { get; private set; }
    public string? DeviceName { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>True while the gate is open, i.e. the user is audibly talking.</summary>
    public bool IsTalking => Gate.IsOpen && !IsSilenced;

    /// <summary>Held down state of the push-to-talk hotkey.</summary>
    public bool PushToTalkHeld { get; set; }

    public bool Muted { get; set; }

    /// <summary>Combined reason the mic is currently producing silence.</summary>
    public bool IsSilenced =>
        Muted || (_settings.Settings.Microphone.PushToTalkEnabled && !PushToTalkHeld);

    public event EventHandler? StateChanged;

    // -----------------------------------------------------------------------

    /// <summary>Start or restart capture using the current settings.</summary>
    public bool Start()
    {
        Stop();

        var mic = _settings.Settings.Microphone;
        if (!mic.PassthroughEnabled)
        {
            Log.Info("Microphone passthrough is disabled.");
            return false;
        }

        lock (_gate)
        {
            try
            {
                var device = _devices.Resolve(mic.InputDeviceId, DeviceKind.Input);
                if (device is null)
                {
                    LastError = "No microphone available.";
                    return false;
                }

                // Capturing a virtual cable while also playing into one feeds our own output
                // straight back in, which builds into a sustained howl. The default capture
                // device on a machine with VB-CABLE installed is often exactly that, so this
                // is an easy trap to fall into by doing nothing at all.
                if (IsVirtualCable(device.FriendlyName) && _engine.VirtualMicBus.IsRunning
                    && IsVirtualCable(_engine.VirtualMicBus.DeviceName))
                {
                    LastError =
                        $"'{device.FriendlyName}' is the listening end of a virtual cable, and the " +
                        $"soundboard is playing into '{_engine.VirtualMicBus.DeviceName}'. Passing it " +
                        "through would feed the soundboard back into itself. Choose your real " +
                        "microphone instead.";

                    Log.Warn(LastError);
                    return false;
                }

                _engineFormat = _engine.VirtualMicBus.Format ?? _engine.MonitorBus.Format;
                if (_engineFormat is null)
                {
                    LastError = "Start an output device before enabling passthrough.";
                    return false;
                }

                // Small capture buffer: this is the half of the round trip we control.
                var bufferMs = _settings.Settings.Audio.LowLatencyMode ? 10 : 25;
                _capture = new WasapiCapture(device, true, bufferMs);

                var format = _capture.WaveFormat;
                _captureChannels = format.Channels;
                _captureRate = format.SampleRate;
                _captureBits = format.BitsPerSample;
                // WASAPI shared mode almost always hands back 32-bit float wrapped in an
                // extensible header, which reports its encoding as Extensible, not IeeeFloat.
                _captureIsFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                    || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);

                ConfigureDsp();

                // Half a second of headroom absorbs scheduling hiccups without adding
                // steady state latency — the ring only ever holds what has not been read.
                var ringSize = _engineFormat.SampleRate * _engineFormat.Channels / 2;

                _virtualRing = new FloatRingBuffer(ringSize);
                _monitorRing = new FloatRingBuffer(ringSize);
                _virtualProvider = new RingSampleProvider(_virtualRing, _engineFormat);
                _monitorProvider = new RingSampleProvider(_monitorRing, _engineFormat)
                {
                    Gain = mic.MonitorVolume
                };

                _previousFrame = new float[_engineFormat.Channels];
                _hasPreviousFrame = false;
                _resamplePosition = 0;

                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();

                if (_engine.VirtualMicBus.IsRunning)
                    _engine.VirtualMicBus.AddVoice(_virtualProvider);

                // Same reasoning as playback: if both buses are the same device, adding the
                // mic to both would double it up and hollow out your own voice.
                if (mic.MonitorEnabled && _engine.MonitorBus.IsRunning && !_engine.OutputsShareDevice)
                    _engine.MonitorBus.AddVoice(_monitorProvider);

                DeviceName = device.FriendlyName;
                IsRunning = true;
                LastError = null;

                Log.Info($"Mic passthrough started on '{DeviceName}' " +
                         $"({_captureRate} Hz, {_captureChannels} ch, {bufferMs} ms).");

                StateChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error("Mic passthrough failed to start", ex);
                CleanUp();
                return false;
            }
        }
    }

    /// <summary>Push the current settings into the DSP blocks without restarting capture.</summary>
    public void ConfigureDsp()
    {
        var mic = _settings.Settings.Microphone;
        var rate = _engineFormat?.SampleRate ?? 48000;

        Gate.Enabled = mic.NoiseGateEnabled;
        Gate.ThresholdDb = mic.GateThresholdDb;
        Gate.AttackMs = mic.GateAttackMs;
        Gate.HoldMs = mic.GateHoldMs;
        Gate.ReleaseMs = mic.GateReleaseMs;
        Gate.Configure(rate);

        Compressor.Enabled = mic.CompressorEnabled;
        Compressor.ThresholdDb = mic.CompressorThresholdDb;
        Compressor.Ratio = mic.CompressorRatio;
        Compressor.Configure(rate);

        Limiter.Enabled = mic.LimiterEnabled;
        Limiter.ThresholdDb = mic.LimiterThresholdDb;
        Limiter.Configure(rate);

        NoiseSuppressor.Enabled = mic.NoiseSuppressionEnabled;
        NoiseSuppressor.Configure(rate);

        if (_monitorProvider is not null) _monitorProvider.Gain = mic.MonitorVolume;
    }

    // -----------------------------------------------------------------------

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed || _engineFormat is null) return;

        try
        {
            var mic = _settings.Settings.Microphone;
            var outChannels = _engineFormat.Channels;

            // 1. bytes -> interleaved float at the capture format
            var captureSamples = BytesToFloat(e.Buffer, e.BytesRecorded, out var captureFrames);
            if (captureFrames == 0) return;

            InputMeter.Process(captureSamples, 0, captureFrames * _captureChannels);

            // 2. channel fold/expand
            var channelMatched = MatchChannels(captureSamples, captureFrames, outChannels,
                mic.ForceMono, out var matchedFrames);

            // 3. sample rate conversion
            var resampled = Resample(channelMatched, matchedFrames, outChannels, out var outFrames);
            if (outFrames == 0) return;

            var sampleCount = outFrames * outChannels;

            // 4. input gain and boost
            var gain = Math.Clamp(mic.InputGain, 0f, 4f) * LevelMeter.FromDb(mic.BoostDb);
            if (Math.Abs(gain - 1f) > 0.0001f)
            {
                for (var i = 0; i < sampleCount; i++) resampled[i] *= gain;
            }

            // 5. processing chain
            NoiseSuppressor.Process(resampled, 0, sampleCount, outChannels);
            Gate.Process(resampled, 0, sampleCount, outChannels);
            Compressor.Process(resampled, 0, sampleCount, outChannels);

            // 6. output gain, mute and push-to-talk
            var outGain = IsSilenced ? 0f : Math.Clamp(mic.OutputGain, 0f, 4f);

            // "Echo cancellation" here means ducking the mic while the soundboard is loud,
            // so the sound does not get re-captured and doubled. It is not a true AEC.
            if (mic.EchoCancellationEnabled && _engine.IsAnythingPlaying)
            {
                var busPeak = _engine.MonitorBus.Meter?.Peak ?? 0f;
                if (busPeak > 0.05f) outGain *= 0.35f;
            }

            if (Math.Abs(outGain - 1f) > 0.0001f)
            {
                for (var i = 0; i < sampleCount; i++) resampled[i] *= outGain;
            }

            Limiter.Process(resampled, 0, sampleCount, outChannels);
            OutputMeter.Process(resampled, 0, sampleCount);

            // 7. hand off to the render threads
            _virtualRing?.Write(resampled, 0, sampleCount);
            if (mic.MonitorEnabled) _monitorRing?.Write(resampled, 0, sampleCount);
        }
        catch (Exception ex)
        {
            Log.Debug($"Mic processing error: {ex.Message}");
        }
    }

    private float[] BytesToFloat(byte[] bytes, int byteCount, out int frames)
    {
        var bytesPerSample = _captureBits / 8;
        if (bytesPerSample <= 0) { frames = 0; return Array.Empty<float>(); }

        var sampleCount = byteCount / bytesPerSample;
        frames = sampleCount / Math.Max(1, _captureChannels);

        EnsureWork(ref _work, sampleCount);

        if (_captureIsFloat && _captureBits == 32)
        {
            Buffer.BlockCopy(bytes, 0, _work, 0, sampleCount * sizeof(float));
        }
        else if (_captureBits == 16)
        {
            for (var i = 0; i < sampleCount; i++)
            {
                var v = BitConverter.ToInt16(bytes, i * 2);
                _work[i] = v / 32768f;
            }
        }
        else if (_captureBits == 24)
        {
            for (var i = 0; i < sampleCount; i++)
            {
                var o = i * 3;
                var v = (bytes[o + 2] << 16) | (bytes[o + 1] << 8) | bytes[o];
                if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                _work[i] = v / 8388608f;
            }
        }
        else if (_captureBits == 32)
        {
            for (var i = 0; i < sampleCount; i++)
                _work[i] = BitConverter.ToInt32(bytes, i * 4) / 2147483648f;
        }
        else
        {
            frames = 0;
            return Array.Empty<float>();
        }

        return _work;
    }

    private float[] _channelBuffer = Array.Empty<float>();

    private float[] MatchChannels(
        float[] source, int frames, int outChannels, bool forceMono, out int outFrames)
    {
        outFrames = frames;

        if (_captureChannels == outChannels && !(forceMono && outChannels == 2))
            return source;

        EnsureWork(ref _channelBuffer, frames * outChannels);

        for (var f = 0; f < frames; f++)
        {
            // Fold everything the device gave us down to one value first.
            var sum = 0f;
            for (var c = 0; c < _captureChannels; c++) sum += source[f * _captureChannels + c];
            var mono = sum / _captureChannels;

            if (forceMono || _captureChannels == 1)
            {
                for (var c = 0; c < outChannels; c++) _channelBuffer[f * outChannels + c] = mono;
            }
            else
            {
                for (var c = 0; c < outChannels; c++)
                {
                    _channelBuffer[f * outChannels + c] = c < _captureChannels
                        ? source[f * _captureChannels + c]
                        : mono;
                }
            }
        }

        return _channelBuffer;
    }

    private float[] _resampleBuffer = Array.Empty<float>();

    private float[] Resample(float[] source, int frames, int channels, out int outFrames)
    {
        if (_captureRate == _engineFormat!.SampleRate)
        {
            outFrames = frames;
            return source;
        }

        var ratio = (double)_captureRate / _engineFormat.SampleRate;
        var estimate = (int)(frames / ratio) + 2;

        EnsureWork(ref _resampleBuffer, estimate * channels);

        var written = 0;
        var position = _resamplePosition;

        while (true)
        {
            var index = (int)Math.Floor(position);
            if (index + 1 >= frames) break;

            var frac = (float)(position - index);

            for (var c = 0; c < channels; c++)
            {
                var a = index < 0
                    ? (_hasPreviousFrame ? _previousFrame[c] : source[c])
                    : source[index * channels + c];
                var b = source[(index + 1) * channels + c];
                _resampleBuffer[written * channels + c] = a + (b - a) * frac;
            }

            written++;
            position += ratio;

            if (written >= estimate) break;
        }

        // Carry the fractional remainder into the next callback so there is no click
        // at buffer boundaries.
        _resamplePosition = position - frames;
        if (_resamplePosition < -1) _resamplePosition = 0;

        if (frames > 0)
        {
            for (var c = 0; c < channels; c++) _previousFrame[c] = source[(frames - 1) * channels + c];
            _hasPreviousFrame = true;
        }

        outFrames = written;
        return _resampleBuffer;
    }

    private static void EnsureWork(ref float[] buffer, int size)
    {
        if (buffer.Length < size) buffer = new float[size + size / 4];
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsRunning = false;
        if (e.Exception is not null)
        {
            LastError = e.Exception.Message;
            Log.Error("Mic capture stopped unexpectedly", e.Exception);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_capture is not null)
            {
                try
                {
                    _capture.DataAvailable -= OnDataAvailable;
                    _capture.RecordingStopped -= OnRecordingStopped;
                    _capture.StopRecording();
                }
                catch (Exception ex) { Log.Debug($"Mic stop: {ex.Message}"); }
            }

            CleanUp();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CleanUp()
    {
        if (_virtualProvider is not null) _engine.VirtualMicBus.RemoveVoice(_virtualProvider);
        if (_monitorProvider is not null) _engine.MonitorBus.RemoveVoice(_monitorProvider);

        try { _capture?.Dispose(); } catch { /* ignore */ }

        _capture = null;
        _virtualProvider = null;
        _monitorProvider = null;
        _virtualRing = null;
        _monitorRing = null;
        _hasPreviousFrame = false;

        Gate.Reset();
        Compressor.Reset();
        Limiter.Reset();
        NoiseSuppressor.Reset();
        InputMeter.Reset();
        OutputMeter.Reset();

        IsRunning = false;
        DeviceName = null;
    }

    /// <summary>Turn passthrough on or off and persist the choice.</summary>
    public void Toggle()
    {
        var mic = _settings.Settings.Microphone;
        mic.PassthroughEnabled = !mic.PassthroughEnabled;
        _settings.Save();

        if (mic.PassthroughEnabled) Start();
        else Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
