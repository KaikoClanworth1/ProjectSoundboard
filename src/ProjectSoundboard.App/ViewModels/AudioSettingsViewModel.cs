using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.Audio;
using ProjectSoundboard.Audio.Dsp;

namespace ProjectSoundboard.App.ViewModels;

/// <summary>Audio routing page: devices, buffers, master processing and live meters.</summary>
public sealed partial class AudioSettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _loading;

    public AudioSettingsViewModel(AppServices services)
    {
        _services = services;
        LoadFromSettings();
        RefreshDevices();
    }

    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();

    public IReadOnlyList<int> SampleRates { get; } = new[] { 44100, 48000, 96000 };
    public IReadOnlyList<string> EqBandNames => Equalizer.BandNames;

    // ---- devices ----------------------------------------------------------

    [ObservableProperty] private AudioDeviceInfo? _virtualMicDevice;
    [ObservableProperty] private AudioDeviceInfo? _monitorDevice;
    [ObservableProperty] private bool _virtualMicEnabled = true;
    [ObservableProperty] private bool _monitorEnabled = true;

    [ObservableProperty] private float _virtualMicVolume = 1f;
    [ObservableProperty] private float _monitorVolume = 0.8f;
    [ObservableProperty] private float _masterVolume = 1f;

    // ---- engine -----------------------------------------------------------

    [ObservableProperty] private bool _lowLatencyMode = true;
    [ObservableProperty] private int _bufferSizeMs = 30;
    [ObservableProperty] private int _sampleRate = 48000;

    // ---- master processing ------------------------------------------------

    [ObservableProperty] private bool _limiterEnabled = true;
    [ObservableProperty] private float _limiterThresholdDb = -1f;
    [ObservableProperty] private bool _compressorEnabled;
    [ObservableProperty] private float _compressorThresholdDb = -18f;
    [ObservableProperty] private float _compressorRatio = 3f;
    [ObservableProperty] private float _compressorAttackMs = 10f;
    [ObservableProperty] private float _compressorReleaseMs = 120f;
    [ObservableProperty] private float _compressorMakeupDb;
    [ObservableProperty] private bool _eqEnabled;

    public ObservableCollection<EqBandViewModel> EqBands { get; } = new();

    // ---- live -------------------------------------------------------------

    [ObservableProperty] private double _virtualMicPeak;
    [ObservableProperty] private double _virtualMicRms;
    [ObservableProperty] private double _virtualMicHold;
    [ObservableProperty] private double _monitorPeak;
    [ObservableProperty] private double _monitorRms;
    [ObservableProperty] private double _monitorHold;
    [ObservableProperty] private double _limiterReductionDb;
    [ObservableProperty] private string _latencyText = "—";
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _virtualMicRunning;
    [ObservableProperty] private bool _monitorRunning;
    [ObservableProperty] private bool _hasVirtualCable;
    [ObservableProperty] private string? _errorText;

    /// <summary>Both outputs are the same Windows device — see the warning on the page.</summary>
    [ObservableProperty] private bool _outputsShareDevice;

    // ---- virtual cable identity -------------------------------------------

    /// <summary>Driver family in use, e.g. "VB-CABLE". Empty when none is installed.</summary>
    [ObservableProperty] private string _cableProduct = string.Empty;

    /// <summary>
    /// The exact device name to select in Discord, VRChat or OBS. Never aliased — the user
    /// has to find this string in another application's device list.
    /// </summary>
    [ObservableProperty] private string? _cableMicrophoneName;

    [ObservableProperty] private bool _hasCompanionMicrophone;
    [ObservableProperty] private bool _isInstallingCable;
    [ObservableProperty] private string? _installStatus;

    /// <summary>
    /// What the playback endpoint is called inside our own UI. Project Soundboard ships no
    /// driver of its own, so this is a label we apply on top of whatever cable is installed.
    /// </summary>
    public string VirtualMicDisplayName => HasVirtualCable && VirtualMicDevice?.IsVirtualCable == true
        ? VirtualCable.OutputAlias
        : VirtualMicDevice?.Name ?? "Not configured";

    /// <summary>The real Windows name, shown underneath the alias so nothing is hidden.</summary>
    public string? VirtualMicRealName => VirtualMicDevice?.Name;

    public bool IsAliased => HasVirtualCable && VirtualMicDevice?.IsVirtualCable == true;

    private readonly VirtualCableInstaller _installer = new();

    private void LoadFromSettings()
    {
        _loading = true;

        var audio = _services.Settings.Settings.Audio;

        VirtualMicEnabled = audio.VirtualMicEnabled;
        MonitorEnabled = audio.MonitorEnabled;
        VirtualMicVolume = audio.VirtualMicVolume;
        MonitorVolume = audio.MonitorVolume;
        MasterVolume = audio.MasterVolume;

        LowLatencyMode = audio.LowLatencyMode;
        BufferSizeMs = audio.BufferSizeMs;
        SampleRate = audio.SampleRate;

        LimiterEnabled = audio.LimiterEnabled;
        LimiterThresholdDb = audio.LimiterThresholdDb;
        CompressorEnabled = audio.CompressorEnabled;
        CompressorThresholdDb = audio.CompressorThresholdDb;
        CompressorRatio = audio.CompressorRatio;
        CompressorAttackMs = audio.CompressorAttackMs;
        CompressorReleaseMs = audio.CompressorReleaseMs;
        CompressorMakeupDb = audio.CompressorMakeupDb;
        EqEnabled = audio.EqEnabled;

        EqBands.Clear();
        for (var i = 0; i < Equalizer.Frequencies.Length; i++)
        {
            var index = i;
            var gain = i < audio.EqBandsDb.Length ? audio.EqBandsDb[i] : 0f;

            var band = new EqBandViewModel(Equalizer.BandNames[i], gain);
            band.GainChanged += (_, value) =>
            {
                if (_loading) return;
                var bands = _services.Settings.Settings.Audio.EqBandsDb;
                if (index < bands.Length) bands[index] = value;
                ApplyProcessing();
            };

            EqBands.Add(band);
        }

        _loading = false;
    }

    public void RefreshDevices()
    {
        var devices = _services.Devices.GetDevices(DeviceKind.Output);

        OutputDevices.Clear();
        foreach (var device in devices) OutputDevices.Add(device);

        HasVirtualCable = devices.Any(d => d.IsVirtualCable);

        var audio = _services.Settings.Settings.Audio;

        _loading = true;

        // Show what is actually in use, not what was configured: with no saved id and no
        // cable installed the engine falls back to the default device, and the combo box
        // should say so rather than sitting empty.
        VirtualMicDevice = devices.FirstOrDefault(d => d.Id == audio.VirtualMicDeviceId)
                           ?? devices.FirstOrDefault(d => d.IsVirtualCable)
                           ?? devices.FirstOrDefault(d => d.Id == _services.Engine.VirtualMicBus.DeviceId)
                           ?? devices.FirstOrDefault(d => d.IsDefault);

        MonitorDevice = devices.FirstOrDefault(d => d.Id == audio.MonitorDeviceId)
                        ?? devices.FirstOrDefault(d => d.Id == _services.Engine.MonitorBus.DeviceId)
                        ?? devices.FirstOrDefault(d => d.IsDefault);

        _loading = false;

        RefreshCableIdentity();
    }

    /// <summary>Work out which cable is in play and what the user must select elsewhere.</summary>
    private void RefreshCableIdentity()
    {
        var cable = VirtualCable.Detect(_services.Devices, VirtualMicDevice?.Id);

        CableProduct = cable?.Product ?? string.Empty;
        CableMicrophoneName = cable?.Microphone?.Name;
        HasCompanionMicrophone = cable?.IsComplete == true;

        OnPropertyChanged(nameof(VirtualMicDisplayName));
        OnPropertyChanged(nameof(VirtualMicRealName));
        OnPropertyChanged(nameof(IsAliased));
        OnPropertyChanged(nameof(VoiceAppInstruction));
    }

    /// <summary>One sentence telling the user exactly what to pick, with the real device name.</summary>
    public string VoiceAppInstruction => HasCompanionMicrophone
        ? $"Set the microphone in Discord, VRChat or OBS to “{CableMicrophoneName}”."
        : HasVirtualCable
            ? "A cable is installed but its recording half could not be found. Check it is enabled " +
              "in Windows sound settings."
            : "Install a virtual audio cable to send sounds into voice chat.";

    // ---- change handlers --------------------------------------------------

    private void ApplyDevices()
    {
        if (_loading) return;

        var audio = _services.Settings.Settings.Audio;
        audio.VirtualMicDeviceId = VirtualMicDevice?.Id;
        audio.MonitorDeviceId = MonitorDevice?.Id;
        audio.VirtualMicEnabled = VirtualMicEnabled;
        audio.MonitorEnabled = MonitorEnabled;
        audio.LowLatencyMode = LowLatencyMode;
        audio.BufferSizeMs = BufferSizeMs;
        audio.SampleRate = SampleRate;

        _services.Settings.Save();
        _services.Engine.ApplyAudioSettings();

        // Passthrough is bound to the output format, so it has to follow a device change.
        if (_services.Settings.Settings.Microphone.PassthroughEnabled)
            _services.Microphone.Start();

        ErrorText = _services.Engine.VirtualMicBus.LastError
                    ?? _services.Engine.MonitorBus.LastError;
    }

    private void ApplyProcessing()
    {
        if (_loading) return;

        var audio = _services.Settings.Settings.Audio;
        audio.VirtualMicVolume = VirtualMicVolume;
        audio.MonitorVolume = MonitorVolume;

        // Master volume belongs to the transport bar. Writing our own copy back here meant
        // that touching any slider on this page reset the master to whatever it happened to
        // be when the page was first built, so the level jumped.
        MasterVolume = audio.MasterVolume;

        audio.LimiterEnabled = LimiterEnabled;
        audio.LimiterThresholdDb = LimiterThresholdDb;
        audio.CompressorEnabled = CompressorEnabled;
        audio.CompressorThresholdDb = CompressorThresholdDb;
        audio.CompressorRatio = CompressorRatio;
        audio.CompressorAttackMs = CompressorAttackMs;
        audio.CompressorReleaseMs = CompressorReleaseMs;
        audio.CompressorMakeupDb = CompressorMakeupDb;
        audio.EqEnabled = EqEnabled;

        _services.Engine.ApplyProcessingSettings();
        _services.Settings.MarkDirty();
    }

    partial void OnVirtualMicDeviceChanged(AudioDeviceInfo? value)
    {
        ApplyDevices();
        RefreshCableIdentity();
    }
    partial void OnMonitorDeviceChanged(AudioDeviceInfo? value) => ApplyDevices();
    partial void OnVirtualMicEnabledChanged(bool value) => ApplyDevices();
    partial void OnMonitorEnabledChanged(bool value) => ApplyDevices();
    partial void OnLowLatencyModeChanged(bool value)
    {
        if (_loading) return;
        // Snap the buffer into the range the chosen mode actually supports.
        BufferSizeMs = value ? Math.Clamp(BufferSizeMs, 5, 40) : Math.Clamp(BufferSizeMs, 20, 200);
        ApplyDevices();
    }
    partial void OnBufferSizeMsChanged(int value) => ApplyDevices();
    partial void OnSampleRateChanged(int value) => ApplyDevices();

    partial void OnVirtualMicVolumeChanged(float value) => ApplyProcessing();
    partial void OnMonitorVolumeChanged(float value) => ApplyProcessing();
    partial void OnLimiterEnabledChanged(bool value) => ApplyProcessing();
    partial void OnLimiterThresholdDbChanged(float value) => ApplyProcessing();
    partial void OnCompressorEnabledChanged(bool value) => ApplyProcessing();
    partial void OnCompressorThresholdDbChanged(float value) => ApplyProcessing();
    partial void OnCompressorRatioChanged(float value) => ApplyProcessing();
    partial void OnCompressorAttackMsChanged(float value) => ApplyProcessing();
    partial void OnCompressorReleaseMsChanged(float value) => ApplyProcessing();
    partial void OnCompressorMakeupDbChanged(float value) => ApplyProcessing();
    partial void OnEqEnabledChanged(bool value) => ApplyProcessing();

    // ---- commands ---------------------------------------------------------

    [RelayCommand]
    private void AutoDetectVirtualMic()
    {
        RefreshDevices();

        var cable = VirtualCable.Detect(_services.Devices);
        if (cable is null)
        {
            StatusText = "No virtual audio cable was found. Install one below, then check again.";
            return;
        }

        VirtualMicDevice = cable.Output;

        StatusText = cable.IsComplete
            ? $"Using {cable.Product} as {VirtualCable.OutputAlias}. " +
              $"In your voice app, choose “{cable.Microphone!.Name}”."
            : $"Using {cable.Product}, but its recording half is not enabled in Windows.";
    }

    [RelayCommand]
    private static void OpenVirtualCableDownload() => VirtualCableInstaller.OpenDownloadPageInBrowser();

    /// <summary>
    /// Fetch VB-Audio's official package, verify its signature and hand it to Windows'
    /// elevation prompt. Project Soundboard never installs a driver silently.
    /// </summary>
    [RelayCommand]
    private async Task InstallVirtualCableAsync()
    {
        if (IsInstallingCable) return;

        IsInstallingCable = true;
        InstallStatus = "Starting…";

        try
        {
            var progress = new Progress<string>(message => InstallStatus = message);
            var outcome = await _installer.RunAsync(progress);

            InstallStatus = outcome switch
            {
                InstallOutcome.InstallerLaunched =>
                    "VB-CABLE's installer is running. When it finishes, reboot if it asks you to, " +
                    "then press “Check again”.",

                InstallOutcome.CancelledByUser =>
                    "Installation needs administrator permission, so nothing was changed. " +
                    "You can try again whenever you like.",

                _ => _installer.LastError is null
                    ? "Opened the VB-CABLE download page in your browser."
                    : $"{_installer.LastError} The download page has been opened instead."
            };
        }
        catch (Exception ex)
        {
            InstallStatus = $"Could not install automatically: {ex.Message}";
        }
        finally
        {
            IsInstallingCable = false;
        }
    }

    /// <summary>Copy the exact device name so it can be pasted into another app's settings.</summary>
    [RelayCommand]
    private void CopyMicrophoneName()
    {
        if (string.IsNullOrWhiteSpace(CableMicrophoneName)) return;

        try
        {
            System.Windows.Clipboard.SetText(CableMicrophoneName);
            StatusText = $"Copied “{CableMicrophoneName}” to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not copy to the clipboard: {ex.Message}";
        }
    }

    [RelayCommand]
    private static void OpenSoundControlPanel()
    {
        try
        {
            Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl") { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private void RestartAudio()
    {
        _services.Engine.ApplyAudioSettings();
        if (_services.Settings.Settings.Microphone.PassthroughEnabled)
            _services.Microphone.Start();

        StatusText = "Audio engine restarted.";
        ErrorText = _services.Engine.VirtualMicBus.LastError ?? _services.Engine.MonitorBus.LastError;
    }

    [RelayCommand]
    private void ResetProcessing()
    {
        _loading = true;
        LimiterEnabled = true;
        LimiterThresholdDb = -1f;
        CompressorEnabled = false;
        CompressorThresholdDb = -18f;
        CompressorRatio = 3f;
        CompressorAttackMs = 10f;
        CompressorReleaseMs = 120f;
        CompressorMakeupDb = 0f;
        EqEnabled = false;

        foreach (var band in EqBands) band.GainDb = 0;
        Array.Clear(_services.Settings.Settings.Audio.EqBandsDb);
        _loading = false;

        ApplyProcessing();
        StatusText = "Master processing reset to defaults.";
    }

    public void UpdateLive()
    {
        var engine = _services.Engine;

        // Reflect master volume changes made from the transport bar.
        MasterVolume = _services.Settings.Settings.Audio.MasterVolume;

        VirtualMicPeak = engine.VirtualMicBus.Meter?.Peak ?? 0;
        VirtualMicRms = engine.VirtualMicBus.Meter?.Rms ?? 0;
        VirtualMicHold = engine.VirtualMicBus.Meter?.PeakHold ?? 0;

        MonitorPeak = engine.MonitorBus.Meter?.Peak ?? 0;
        MonitorRms = engine.MonitorBus.Meter?.Rms ?? 0;
        MonitorHold = engine.MonitorBus.Meter?.PeakHold ?? 0;

        LimiterReductionDb = engine.VirtualMicBus.Limiter?.GainReductionDb ?? 0;

        VirtualMicRunning = engine.VirtualMicBus.IsRunning;
        MonitorRunning = engine.MonitorBus.IsRunning;
        OutputsShareDevice = engine.OutputsShareDevice;

        LatencyText = VirtualMicRunning || MonitorRunning
            ? $"{engine.EstimatedLatencyMs} ms round trip"
            : "Not running";
    }
}

/// <summary>One EQ band slider.</summary>
public sealed partial class EqBandViewModel : ObservableObject
{
    public EqBandViewModel(string name, float gainDb)
    {
        Name = name;
        _gainDb = gainDb;
    }

    public string Name { get; }

    [ObservableProperty]
    private float _gainDb;

    public event EventHandler<float>? GainChanged;

    partial void OnGainDbChanged(float value) => GainChanged?.Invoke(this, value);
}
