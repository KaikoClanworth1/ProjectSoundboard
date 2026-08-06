using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.App.Views;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Global\\ProjectSoundboard.SingleInstance";

    private Mutex? _instanceMutex;
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second copy would fight the first one for the virtual cable and the hotkeys.
        _instanceMutex = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Project Soundboard is already running.\n\nLook for it in the system tray.",
                "Project Soundboard", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        try
        {
            _services = AppServices.Initialise();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Project Soundboard could not start.\n\n{ex.Message}",
                "Startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _services.Theme.Apply();

        Log.Info("Project Soundboard starting.");

        if (!_services.Settings.Settings.SetupCompleted)
        {
            var wizard = new SetupWizardWindow();
            var completed = wizard.ShowDialog() == true;

            if (!completed)
            {
                // The user backed out of first-run setup — there is nothing usable to show.
                Log.Info("Setup wizard cancelled; exiting.");
                Shutdown();
                return;
            }
        }

        _services.StartAudio();

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        var general = _services.Settings.Settings.General;

        if (general.StartMinimized)
        {
            // Minimised means on the taskbar, unless the user specifically wants the tray.
            main.WindowState = WindowState.Minimized;
            main.Show();

            if (general.MinimizeToTray) main.Hide();
        }
        else
        {
            main.Show();
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);

        var result = MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\n" +
            "Project Soundboard can usually keep running. Continue?",
            "Unexpected error", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        // Marking it handled keeps a single bad view from taking the whole app down.
        e.Handled = result == MessageBoxResult.Yes;
        if (!e.Handled) Shutdown();
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) Log.Error("Unhandled background exception", ex);
        else Log.Error($"Unhandled background exception: {e.ExceptionObject}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _services?.Dispose(); }
        catch { /* nothing useful left to do */ }

        try { _instanceMutex?.ReleaseMutex(); } catch { /* not owned */ }
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
