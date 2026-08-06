using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.Audio;

namespace ProjectSoundboard.App.ViewModels;

/// <summary>
/// Microphone passthrough page: pick the mic, shape it, and send it down the same virtual
/// cable as the soundboard so voice apps only need one device selected.
/// </summary>
public sealed partial class MicrophoneViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _loading;

    public MicrophoneViewModel(AppServices services)
    {
        _services = services;
        LoadFromSettings();
        RefreshDevices();
    }

    public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = new();

    [ObservableProperty] private AudioDeviceInfo? _inputDevice;

    [ObservableProperty] private bool _passthroughEnabled;
    [ObservableProperty] private bool _monitorEnabled;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private bool _forceMono = true;
    [ObservableProperty] private bool _pushToTalkEnabled;

    [ObservableProperty] private float _inputGain = 1f;
    [ObservableProperty] private float _outputGain = 1f;
    [ObservableProperty] private float _monitorVolume = 0.4f;
    [ObservableProperty] private float _boostDb;

    [ObservableProperty] private bool _noiseGateEnabled = true;
    [ObservableProperty] private float _gateThresholdDb = -45f;
    [ObservableProperty] private float _gateAttackMs = 5f;
    [ObservableProperty] private float _gateHoldMs = 120f;
    [ObservableProperty] private float _gateReleaseMs = 180f;

    [ObservableProperty] private bool _compressorEnabled;
    [ObservableProperty] private float _compressorThresholdDb = -20f;
    [ObservableProperty] private float _compressorRatio = 4f;

    [ObservableProperty] private bool _limiterEnabled = true;
    [ObservableProperty] private float _limiterThresholdDb = -1.5f;

    [ObservableProperty] private bool _noiseSuppressionEnabled;
    [ObservableProperty] private bool _echoCancellationEnabled;

    // ---- live -------------------------------------------------------------

    [ObservableProperty] private double _inputPeak;
    [ObservableProperty] private double _inputRms;
    [ObservableProperty] private double _outputPeak;
    [ObservableProperty] private double _outputRms;
    [ObservableProperty] private bool _isTalking;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _pushToTalkHeld;
    [ObservableProperty] private string _statusText = "Passthrough is off.";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private double _noiseFloorDb;

    /// <summary>Where the gate threshold sits on the meter, 0..1, so it can be drawn on it.</summary>
    public double GateThresholdFraction =>
        Math.Clamp((GateThresholdDb + 54) / 54.0, 0, 1);

    private void LoadFromSettings()
    {
        _loading = true;
        var mic = _services.Settings.Settings.Microphone;

        PassthroughEnabled = mic.PassthroughEnabled;
        MonitorEnabled = mic.MonitorEnabled;
        Muted = mic.Muted;
        ForceMono = mic.ForceMono;
        PushToTalkEnabled = mic.PushToTalkEnabled;

        InputGain = mic.InputGain;
        OutputGain = mic.OutputGain;
        MonitorVolume = mic.MonitorVolume;
        BoostDb = mic.BoostDb;

        NoiseGateEnabled = mic.NoiseGateEnabled;
        GateThresholdDb = mic.GateThresholdDb;
        GateAttackMs = mic.GateAttackMs;
        GateHoldMs = mic.GateHoldMs;
        GateReleaseMs = mic.GateReleaseMs;

        CompressorEnabled = mic.CompressorEnabled;
        CompressorThresholdDb = mic.CompressorThresholdDb;
        CompressorRatio = mic.CompressorRatio;

        LimiterEnabled = mic.LimiterEnabled;
        LimiterThresholdDb = mic.LimiterThresholdDb;

        NoiseSuppressionEnabled = mic.NoiseSuppressionEnabled;
        EchoCancellationEnabled = mic.EchoCancellationEnabled;

        _loading = false;
    }

    public void RefreshDevices()
    {
        var devices = _services.Devices.GetDevices(DeviceKind.Input);

        InputDevices.Clear();
        foreach (var device in devices) InputDevices.Add(device);

        var saved = _services.Settings.Settings.Microphone.InputDeviceId;

        _loading = true;
        // Same reasoning as the setup wizard: a virtual cable's output is a capture device,
        // and often the system default, but passing it through loops the soundboard back in.
        InputDevice = devices.FirstOrDefault(d => d.Id == saved)
                      ?? devices.FirstOrDefault(d => d.IsDefault && !d.IsVirtualCable)
                      ?? devices.FirstOrDefault(d => !d.IsVirtualCable)
                      ?? devices.FirstOrDefault();
        _loading = false;
    }

    private void ApplyDsp()
    {
        if (_loading) return;

        var mic = _services.Settings.Settings.Microphone;

        mic.MonitorEnabled = MonitorEnabled;
        mic.Muted = Muted;
        mic.ForceMono = ForceMono;
        mic.InputGain = InputGain;
        mic.OutputGain = OutputGain;
        mic.MonitorVolume = MonitorVolume;
        mic.BoostDb = BoostDb;

        mic.NoiseGateEnabled = NoiseGateEnabled;
        mic.GateThresholdDb = GateThresholdDb;
        mic.GateAttackMs = GateAttackMs;
        mic.GateHoldMs = GateHoldMs;
        mic.GateReleaseMs = GateReleaseMs;

        mic.CompressorEnabled = CompressorEnabled;
        mic.CompressorThresholdDb = CompressorThresholdDb;
        mic.CompressorRatio = CompressorRatio;

        mic.LimiterEnabled = LimiterEnabled;
        mic.LimiterThresholdDb = LimiterThresholdDb;

        mic.NoiseSuppressionEnabled = NoiseSuppressionEnabled;
        mic.EchoCancellationEnabled = EchoCancellationEnabled;

        _services.Microphone.Muted = Muted;
        _services.Microphone.ConfigureDsp();
        _services.Settings.MarkDirty();

        OnPropertyChanged(nameof(GateThresholdFraction));
    }

    /// <summary>Changes that need capture to be torn down and restarted.</summary>
    private void ApplyDevice()
    {
        if (_loading) return;

        var mic = _services.Settings.Settings.Microphone;
        mic.InputDeviceId = InputDevice?.Id;
        mic.PassthroughEnabled = PassthroughEnabled;
        mic.MonitorEnabled = MonitorEnabled;
        _services.Settings.Save();

        if (PassthroughEnabled)
        {
            _services.Microphone.Start();
            ErrorText = _services.Microphone.LastError;
            StatusText = _services.Microphone.IsRunning
                ? $"Live on '{_services.Microphone.DeviceName}'."
                : "Passthrough could not start.";
        }
        else
        {
            _services.Microphone.Stop();
            ErrorText = null;
            StatusText = "Passthrough is off.";
        }
    }

    partial void OnInputDeviceChanged(AudioDeviceInfo? value) => ApplyDevice();
    partial void OnPassthroughEnabledChanged(bool value) => ApplyDevice();
    partial void OnMonitorEnabledChanged(bool value) => ApplyDevice();

    partial void OnMutedChanged(bool value) => ApplyDsp();
    partial void OnForceMonoChanged(bool value) => ApplyDsp();
    partial void OnInputGainChanged(float value) => ApplyDsp();
    partial void OnOutputGainChanged(float value) => ApplyDsp();
    partial void OnMonitorVolumeChanged(float value) => ApplyDsp();
    partial void OnBoostDbChanged(float value) => ApplyDsp();
    partial void OnNoiseGateEnabledChanged(bool value) => ApplyDsp();
    partial void OnGateThresholdDbChanged(float value) => ApplyDsp();
    partial void OnGateAttackMsChanged(float value) => ApplyDsp();
    partial void OnGateHoldMsChanged(float value) => ApplyDsp();
    partial void OnGateReleaseMsChanged(float value) => ApplyDsp();
    partial void OnCompressorEnabledChanged(bool value) => ApplyDsp();
    partial void OnCompressorThresholdDbChanged(float value) => ApplyDsp();
    partial void OnCompressorRatioChanged(float value) => ApplyDsp();
    partial void OnLimiterEnabledChanged(bool value) => ApplyDsp();
    partial void OnLimiterThresholdDbChanged(float value) => ApplyDsp();
    partial void OnNoiseSuppressionEnabledChanged(bool value) => ApplyDsp();
    partial void OnEchoCancellationEnabledChanged(bool value) => ApplyDsp();

    partial void OnPushToTalkEnabledChanged(bool value)
    {
        if (_loading) return;
        _services.Settings.Settings.Microphone.PushToTalkEnabled = value;
        _services.Settings.Save();
        _services.Hotkeys.RefreshPushToTalk();
    }

    [RelayCommand]
    private void TogglePassthrough() => PassthroughEnabled = !PassthroughEnabled;

    [RelayCommand]
    private void ToggleMute() => Muted = !Muted;

    /// <summary>
    /// Watch the room for a moment and park the gate threshold just above the noise floor.
    /// </summary>
    [RelayCommand]
    private async Task CalibrateGateAsync()
    {
        if (!_services.Microphone.IsRunning)
        {
            StatusText = "Turn passthrough on first, then stay quiet while it listens.";
            return;
        }

        StatusText = "Listening to the room — stay quiet for 3 seconds…";

        var samples = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            samples.Add(_services.Microphone.InputMeter.Peak);
        }

        // Use a high percentile of the quiet floor rather than the maximum, so one cough
        // does not push the threshold up above speech.
        samples.Sort();
        var floor = samples[(int)(samples.Count * 0.9)];
        var floorDb = floor <= 0 ? -60 : 20 * Math.Log10(floor);

        GateThresholdDb = (float)Math.Clamp(floorDb + 8, -60, -15);
        StatusText = $"Gate set to {GateThresholdDb:0.#} dB, just above the room noise.";
    }

    [RelayCommand]
    private void ResetProcessing()
    {
        _loading = true;
        InputGain = 1f;
        OutputGain = 1f;
        BoostDb = 0f;
        NoiseGateEnabled = true;
        GateThresholdDb = -45f;
        GateAttackMs = 5f;
        GateHoldMs = 120f;
        GateReleaseMs = 180f;
        CompressorEnabled = false;
        CompressorThresholdDb = -20f;
        CompressorRatio = 4f;
        LimiterEnabled = true;
        LimiterThresholdDb = -1.5f;
        NoiseSuppressionEnabled = false;
        EchoCancellationEnabled = false;
        _loading = false;

        ApplyDsp();
        StatusText = "Microphone processing reset to defaults.";
    }

    public void UpdateLive()
    {
        var mic = _services.Microphone;

        InputPeak = mic.InputMeter.Peak;
        InputRms = mic.InputMeter.Rms;
        OutputPeak = mic.OutputMeter.Peak;
        OutputRms = mic.OutputMeter.Rms;
        IsTalking = mic.IsRunning && mic.IsTalking;
        IsRunning = mic.IsRunning;
        PushToTalkHeld = mic.PushToTalkHeld;
        NoiseFloorDb = mic.NoiseSuppressor.EstimatedNoiseFloorDb;
    }
}
