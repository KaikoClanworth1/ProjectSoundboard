using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.Audio;
using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.ViewModels;

public enum WizardStep
{
    Welcome,
    Theme,
    Folders,
    VirtualMic,
    Monitor,
    Microphone,
    VoiceApps,
    Hotkeys,
    Test,
    Finish
}

/// <summary>
/// First run setup. Every step has a working default, so a user who mashes Next still ends
/// up with a soundboard that plays sound somewhere.
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject
{
    private readonly AppServices _services;

    private static readonly WizardStep[] Order =
    {
        WizardStep.Welcome, WizardStep.Theme, WizardStep.Folders, WizardStep.VirtualMic,
        WizardStep.Monitor, WizardStep.Microphone, WizardStep.VoiceApps, WizardStep.Hotkeys,
        WizardStep.Test, WizardStep.Finish
    };

    public SetupWizardViewModel(AppServices services)
    {
        _services = services;

        MainLibraryPath = services.Settings.Settings.Library.MainLibraryPath
                          ?? AppPaths.DefaultMainLibrary;

        Theme = services.Settings.Settings.Appearance.Theme;

        foreach (var folder in services.Settings.Settings.Library.Folders)
            Folders.Add(folder.Path);

        RefreshDevices();
    }

    [ObservableProperty] private WizardStep _step = WizardStep.Welcome;
    [ObservableProperty] private string _statusText = string.Empty;

    public int StepNumber => Array.IndexOf(Order, Step) + 1;
    public int StepCount => Order.Length;
    public double ProgressFraction => (double)StepNumber / StepCount;
    public bool CanGoBack => StepNumber > 1;
    public bool IsLastStep => Step == WizardStep.Finish;

    public string NextButtonText => IsLastStep ? "Start using Project Soundboard" : "Next";

    partial void OnStepChanged(WizardStep value)
    {
        OnPropertyChanged(nameof(StepNumber));
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextButtonText));
        StatusText = string.Empty;
    }

    // ---- theme ------------------------------------------------------------

    [ObservableProperty] private AppTheme _theme = AppTheme.Dark;

    partial void OnThemeChanged(AppTheme value)
    {
        _services.Settings.Settings.Appearance.Theme = value;
        _services.Theme.Apply();
    }

    [RelayCommand]
    private void ChooseTheme(string? name)
    {
        if (Enum.TryParse<AppTheme>(name, true, out var theme)) Theme = theme;
    }

    // ---- folders ----------------------------------------------------------

    public ObservableCollection<string> Folders { get; } = new();

    [ObservableProperty] private string _mainLibraryPath = string.Empty;

    public bool HasFolders => Folders.Count > 0;

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder that contains sounds" };
        if (dialog.ShowDialog() != true) return;

        if (Folders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
        {
            StatusText = "That folder is already on the list.";
            return;
        }

        Folders.Add(dialog.FolderName);
        OnPropertyChanged(nameof(HasFolders));
        StatusText = string.Empty;
    }

    [RelayCommand]
    private void RemoveFolder(string? path)
    {
        if (path is null) return;
        Folders.Remove(path);
        OnPropertyChanged(nameof(HasFolders));
    }

    [RelayCommand]
    private void ChooseMainLibrary()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Where should imported sounds be kept?",
            InitialDirectory = Directory.Exists(MainLibraryPath)
                ? MainLibraryPath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };

        if (dialog.ShowDialog() != true) return;
        MainLibraryPath = dialog.FolderName;
    }

    [RelayCommand]
    private void UseSuggestedLibrary() => MainLibraryPath = AppPaths.DefaultMainLibrary;

    // ---- devices ----------------------------------------------------------

    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = new();

    [ObservableProperty] private AudioDeviceInfo? _virtualMicDevice;
    [ObservableProperty] private AudioDeviceInfo? _monitorDevice;
    [ObservableProperty] private AudioDeviceInfo? _inputDevice;
    [ObservableProperty] private bool _passthroughEnabled = true;
    [ObservableProperty] private bool _hasVirtualCable;
    [ObservableProperty] private bool _lowLatencyMode = true;

    /// <summary>Exact device name the user must select in their voice app. Never aliased.</summary>
    [ObservableProperty] private string? _cableMicrophoneName;

    [ObservableProperty] private string _cableProduct = string.Empty;
    [ObservableProperty] private bool _isInstallingCable;
    [ObservableProperty] private string? _installStatus;

    private readonly VirtualCableInstaller _installer = new();

    public bool HasCompanionMicrophone => !string.IsNullOrEmpty(CableMicrophoneName);

    /// <summary>Reads the real device name when we know it, generic advice when we do not.</summary>
    public string VoiceAppInstruction => HasCompanionMicrophone
        ? $"Choose “{CableMicrophoneName}” as the microphone."
        : "Choose your cable's recording device as the microphone.";

    [RelayCommand]
    public void RefreshDevices()
    {
        var outputs = _services.Devices.GetDevices(DeviceKind.Output);
        var inputs = _services.Devices.GetDevices(DeviceKind.Input);

        OutputDevices.Clear();
        foreach (var d in outputs) OutputDevices.Add(d);

        InputDevices.Clear();
        foreach (var d in inputs) InputDevices.Add(d);

        HasVirtualCable = outputs.Any(d => d.IsVirtualCable);

        // With no cable installed, fall back to the default output rather than leaving the
        // field blank. Sounds then play to the speakers only, which the warning card above
        // already explains — an empty required-looking dropdown just reads as broken.
        VirtualMicDevice ??= outputs.FirstOrDefault(d => d.IsVirtualCable)
                             ?? outputs.FirstOrDefault(d => d.IsDefault)
                             ?? outputs.FirstOrDefault();

        MonitorDevice ??= outputs.FirstOrDefault(d => d.IsDefault && !d.IsVirtualCable)
                          ?? outputs.FirstOrDefault(d => !d.IsVirtualCable);

        // Never default to a cable's listening end: on a machine with VB-CABLE installed it
        // is frequently the system default input, and passing it through would loop the
        // soundboard back into itself.
        InputDevice ??= inputs.FirstOrDefault(d => d.IsDefault && !d.IsVirtualCable)
                        ?? inputs.FirstOrDefault(d => !d.IsVirtualCable)
                        ?? inputs.FirstOrDefault();

        var cable = VirtualCable.Detect(_services.Devices, VirtualMicDevice?.Id);
        CableProduct = cable?.Product ?? string.Empty;
        CableMicrophoneName = cable?.Microphone?.Name;

        OnPropertyChanged(nameof(HasCompanionMicrophone));
        OnPropertyChanged(nameof(VoiceAppInstruction));

        StatusText = cable is null
            ? "No virtual audio cable detected yet — sounds will play to your speakers only."
            : $"Found {cable.Product}: “{cable.Output.Name}”.";
    }

    [RelayCommand]
    private static void OpenVirtualCableDownload() => VirtualCableInstaller.OpenDownloadPageInBrowser();

    /// <summary>
    /// Download VB-Audio's official package, verify its signature, and let Windows ask for
    /// permission to install it. Nothing is bundled and nothing is installed silently.
    /// </summary>
    [RelayCommand]
    private async Task InstallVirtualCableAsync()
    {
        if (IsInstallingCable) return;

        IsInstallingCable = true;
        InstallStatus = "Starting…";

        try
        {
            var progress = new Progress<string>(m => InstallStatus = m);
            var outcome = await _installer.RunAsync(progress);

            InstallStatus = outcome switch
            {
                InstallOutcome.InstallerLaunched =>
                    "VB-CABLE's installer is running. Finish it (rebooting if it asks), then press " +
                    "“Check again” to pick the new device up.",

                InstallOutcome.CancelledByUser =>
                    "Installing a driver needs administrator permission, so nothing was changed.",

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

    // ---- hotkeys ----------------------------------------------------------

    [ObservableProperty] private bool _enableDefaultHotkeys = true;

    public string DefaultHotkeyDescription =>
        "F10 — toggle microphone passthrough\n" +
        "F11 — mute the soundboard\n" +
        "F12 — mute your microphone\n" +
        "Ctrl + Esc — stop every sound";

    // ---- test -------------------------------------------------------------

    [ObservableProperty] private double _testVirtualPeak;
    [ObservableProperty] private double _testMonitorPeak;
    [ObservableProperty] private double _testMicPeak;
    [ObservableProperty] private bool _testRan;
    [ObservableProperty] private string _testResultText = string.Empty;

    /// <summary>
    /// Apply everything chosen so far so the test step exercises the real signal path
    /// rather than a simulation.
    /// </summary>
    public void ApplyBeforeTest()
    {
        Commit(saveSetupFlag: false);
        _services.StartAudio();
    }

    [RelayCommand]
    private void PlayTestTone()
    {
        var sound = _services.Library.Sounds.FirstOrDefault(s => !s.IsMissing && !s.IsBroken);

        if (sound is null)
        {
            TestResultText =
                "There are no sounds in your library yet, so there is nothing to play. " +
                "Add a folder on the earlier step, or just drag some files in once setup finishes.";
            TestRan = true;
            return;
        }

        var handle = _services.Engine.Play(sound);

        TestRan = true;
        TestResultText = handle is null
            ? "Nothing played. Check the device choices on the previous steps."
            : $"Playing “{sound.DisplayName}”. Watch the meters below — the virtual mic meter " +
              "is what your friends will hear.";
    }

    [RelayCommand]
    private void StopTest() => _services.Engine.StopAll();

    /// <summary>Called from the window's timer while the test step is visible.</summary>
    public void UpdateMeters()
    {
        TestVirtualPeak = _services.Engine.VirtualMicBus.Meter?.Peak ?? 0;
        TestMonitorPeak = _services.Engine.MonitorBus.Meter?.Peak ?? 0;
        TestMicPeak = _services.Microphone.InputMeter.Peak;
    }

    // ---- navigation -------------------------------------------------------

    public void Next()
    {
        if (Step == WizardStep.Folders && !HasFolders)
        {
            // Not fatal — the user can drag sounds in later — but say so plainly.
            StatusText = "No folders chosen. You can still drag sounds in once setup finishes.";
        }

        var index = Array.IndexOf(Order, Step);
        if (index < Order.Length - 1) Step = Order[index + 1];

        if (Step == WizardStep.Test) ApplyBeforeTest();
    }

    public void Back()
    {
        var index = Array.IndexOf(Order, Step);
        if (index > 0) Step = Order[index - 1];
    }

    /// <summary>Write every choice into settings.</summary>
    public void Commit(bool saveSetupFlag = true)
    {
        var settings = _services.Settings.Settings;

        settings.Appearance.Theme = Theme;

        // Library folders
        settings.Library.Folders.RemoveAll(f => !f.IsMainLibrary);
        foreach (var path in Folders)
        {
            if (settings.Library.Folders.Any(f =>
                    string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            settings.Library.Folders.Add(new LibraryFolder { Path = path, Recursive = true, Watch = true });
        }

        settings.Library.MainLibraryPath = MainLibraryPath;

        // Audio
        settings.Audio.VirtualMicDeviceId = VirtualMicDevice?.Id;
        settings.Audio.MonitorDeviceId = MonitorDevice?.Id;
        settings.Audio.VirtualMicEnabled = VirtualMicDevice is not null;
        settings.Audio.MonitorEnabled = MonitorDevice is not null;
        settings.Audio.LowLatencyMode = LowLatencyMode;
        settings.Audio.BufferSizeMs = LowLatencyMode ? 20 : 60;

        // Microphone
        settings.Microphone.InputDeviceId = InputDevice?.Id;
        settings.Microphone.PassthroughEnabled = PassthroughEnabled && InputDevice is not null;

        if (!EnableDefaultHotkeys) settings.Hotkeys.Clear();

        if (saveSetupFlag) settings.SetupCompleted = true;

        _services.Settings.Save();

        try
        {
            Directory.CreateDirectory(MainLibraryPath);
            _services.Import.EnsureMainLibraryPath();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not create the main library folder: {ex.Message}");
        }

        Log.Info($"Setup committed: {Folders.Count} folder(s), " +
                 $"virtual mic '{VirtualMicDevice?.Name ?? "none"}', " +
                 $"monitor '{MonitorDevice?.Name ?? "none"}'.");
    }
}
