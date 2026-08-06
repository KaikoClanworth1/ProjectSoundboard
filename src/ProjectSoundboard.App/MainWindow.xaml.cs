using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.App.ViewModels;
using ProjectSoundboard.App.Views;
using ProjectSoundboard.Core.Storage;
using Forms = System.Windows.Forms;

namespace ProjectSoundboard.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly MainViewModel _viewModel;
    private Forms.NotifyIcon? _tray;
    private bool _reallyClosing;

    public MainWindow()
    {
        InitializeComponent();

        _services = AppServices.Current;
        _viewModel = new MainViewModel(_services);
        DataContext = _viewModel;

        _services.Hotkeys.HotkeyPressed += OnHotkeyPressed;
        _services.Hotkeys.PushToTalkChanged += OnPushToTalkChanged;
        _viewModel.Settings.ScaleChanged += (_, _) => ApplyScale();
        _viewModel.ToggleWindowRequested += (_, _) => Dispatcher.Invoke(ToggleVisibility);

        SourceInitialized += OnSourceInitialised;
        Loaded += OnLoaded;

        ApplyScale();
        ApplyBranding();
        SetUpTray();
    }

    /// <summary>
    /// Swap the drawn placeholder for the real logo when the artwork is present. The window
    /// and taskbar icons come from the executable's own icon, which WPF picks up for free.
    /// </summary>
    private void ApplyBranding()
    {
        if (Branding.Logo is null) return;

        LogoImage.Source = Branding.Logo;
        LogoImage.Visibility = Visibility.Visible;
        LogoFallback.Visibility = Visibility.Collapsed;
    }

    public MainViewModel ViewModel => _viewModel;

    private void OnSourceInitialised(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _services.Hotkeys.Attach(handle);
        _viewModel.Hotkeys.Reload();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The engine reports device problems as soon as it tries to open them; surface
        // the first one instead of leaving the user with a silent soundboard.
        var error = _services.Engine.VirtualMicBus.LastError ?? _services.Engine.MonitorBus.LastError;
        if (error is not null) _viewModel.StatusMessage = $"Audio: {error}";

        await CheckForUpdatesOnStartupAsync();
    }

    /// <summary>
    /// Quietly look for a new release. Anything that goes wrong here — offline, rate limited,
    /// no repository configured — is deliberately silent: an update check failing is not the
    /// user's problem to solve on launch.
    /// </summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (!_services.Updates.ShouldCheckOnStartup()) return;

        try
        {
            // Let the window settle before doing network work.
            await Task.Delay(TimeSpan.FromSeconds(3));

            var update = await _services.Updates.CheckAsync();
            if (update is null || !IsLoaded) return;

            new UpdateDialog(_services.Updates, update) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Debug($"Startup update check failed: {ex.Message}");
        }
    }

    private void ApplyScale()
    {
        var scale = _services.Theme.EffectiveScale;
        RootScaleTransform.ScaleX = scale;
        RootScaleTransform.ScaleY = scale;
    }

    // -----------------------------------------------------------------------
    // Title bar
    // -----------------------------------------------------------------------

    private void OnMinimiseClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximiseClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Minimized &&
            _services.Settings.Settings.General.MinimizeToTray)
        {
            Hide();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClosing && _services.Settings.Settings.General.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            _tray?.ShowBalloonTip(2000, "Project Soundboard",
                "Still running in the tray. Right-click the icon to quit.", Forms.ToolTipIcon.Info);
            return;
        }

        _services.Hotkeys.HotkeyPressed -= OnHotkeyPressed;
        _services.Hotkeys.PushToTalkChanged -= OnPushToTalkChanged;

        _viewModel.Dispose();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        base.OnClosing(e);
    }

    // -----------------------------------------------------------------------
    // Tray
    // -----------------------------------------------------------------------

    private void SetUpTray()
    {
        try
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Show Project Soundboard", null, (_, _) => ShowFromTray());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Stop all sounds", null, (_, _) => _services.Engine.StopAll());
            menu.Items.Add("Mute soundboard", null, (_, _) =>
                _services.Engine.SetSoundboardMuted(!_services.Engine.SoundboardMuted));
            menu.Items.Add("Toggle mic passthrough", null, (_, _) => _services.Microphone.Toggle());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) =>
            {
                _reallyClosing = true;
                Dispatcher.Invoke(Close);
            });

            // Render the tray icon at whatever size this machine's tray actually uses,
            // rather than letting Windows downscale a 256 px frame into mush.
            var trayIcon = Branding.CreateTrayIcon(Forms.SystemInformation.SmallIconSize.Width)
                           ?? System.Drawing.SystemIcons.Application;

            _tray = new Forms.NotifyIcon
            {
                Icon = trayIcon,
                Text = "Project Soundboard",
                Visible = true,
                ContextMenuStrip = menu
            };

            _tray.DoubleClick += (_, _) => ShowFromTray();
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray icon unavailable: {ex.Message}");
        }
    }

    private void ShowFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        });
    }

    private void ToggleVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized) Hide();
        else ShowFromTray();
    }

    // -----------------------------------------------------------------------
    // Global hotkeys
    // -----------------------------------------------------------------------

    private void OnHotkeyPressed(object? sender, Core.Models.HotkeyBinding binding) =>
        Dispatcher.BeginInvoke(() => _viewModel.HandleHotkey(binding));

    private void OnPushToTalkChanged(object? sender, bool held) => _viewModel.HandlePushToTalk(held);

    // -----------------------------------------------------------------------
    // Drag and drop import
    // -----------------------------------------------------------------------

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        UpdateDragFeedback(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        UpdateDragFeedback(e);
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void UpdateDragFeedback(DragEventArgs e)
    {
        var accepts = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = accepts ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    protected override async void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        DropOverlay.Visibility = Visibility.Collapsed;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        e.Handled = true;

        try
        {
            await ImportFlow.RunAsync(this, _services, paths, _viewModel);
        }
        catch (Exception ex)
        {
            Log.Error("Import failed", ex);
            MessageBox.Show($"The import could not be completed.\n\n{ex.Message}",
                "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
