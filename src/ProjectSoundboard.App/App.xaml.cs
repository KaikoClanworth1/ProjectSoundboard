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
    private UiHangDetector? _hangDetector;
    private string? _lastCrashReport;

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
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

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

        // Applied before anything is drawn, because it cannot be changed once rendering has
        // started. Turning it off drops WPF to software rendering, which uses no graphics
        // memory at all — worth having on a machine that would rather keep its video memory
        // for the game the soundboard is being used alongside. The setting existed but was
        // never read, so it did nothing.
        if (!_services.Settings.Settings.Performance.HardwareAcceleration)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode =
                System.Windows.Interop.RenderMode.SoftwareOnly;

            Log.Info("Hardware acceleration is off; drawing in software.");
        }

        Log.Info("Project Soundboard starting.");

        // Everything a report needs that only the app layer can see.
        CrashReporter.DescribeEnvironment = _services.DescribeForCrashReport;
        CrashReporter.DescribePostMortem = WindowsErrorReport.Describe;

        // Leaves a marker for the whole run. If the next start finds it, this run died
        // without shutting down — which is the only way the failures that kill the process
        // outright can be recorded at all.
        var previousSession = CrashReporter.BeginSession(UpdateService.CurrentVersion.ToString(), Log.CurrentFile);

        if (previousSession is not null)
        {
            Log.Warn($"The previous run (process {previousSession.ProcessId}, version " +
                     $"{previousSession.Version}) ended without shutting down.");
        }

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

        // Written only now, with the audio stack up: the setup section is the useful half of
        // this report, and before StartAudio every device reads as "none".
        if (previousSession is not null)
            _lastCrashReport = CrashReporter.WriteUncleanShutdown(previousSession);

        // From here on the interface is watched from outside. A frozen app otherwise leaves
        // no trace at all: nothing thrown, nothing caught, and a log that just stops.
        _hangDetector = new UiHangDetector(Dispatcher);
        _hangDetector.Start();

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Say it in the status strip rather than with a dialog. A crash is worth mentioning,
        // but not worth a box in the way every time the app is opened afterwards.
        if (_lastCrashReport is not null)
        {
            main.ViewModel.StatusMessage =
                "Project Soundboard did not shut down properly last time. A report was saved — " +
                "see Settings, under Crash reports.";

            main.ViewModel.Settings.RefreshCrashReports();
        }

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
        var report = CrashReporter.WriteException("the interface", e.Exception, UpdateService.CurrentVersion.ToString());

        var result = MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\n" +
            (report is null ? "" : $"A report was saved to:\n{report}\n\n") +
            "Project Soundboard can usually keep running. Continue?",
            "Unexpected error", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        // Marking it handled keeps a single bad view from taking the whole app down.
        e.Handled = result == MessageBoxResult.Yes;
        if (!e.Handled) Shutdown();
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Log.Error("Unhandled background exception", ex);
            CrashReporter.WriteException("a background thread", ex, UpdateService.CurrentVersion.ToString());
        }
        else
        {
            Log.Error($"Unhandled background exception: {e.ExceptionObject}");
        }
    }

    /// <summary>
    /// A task whose exception nobody ever looked at. Harmless to the run, but it is usually
    /// the first sign of something that will matter later, so it is worth writing down.
    /// </summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Unobserved background task exception", e.Exception);
        CrashReporter.WriteException("a background task", e.Exception, UpdateService.CurrentVersion.ToString());

        // Nothing is broken yet; do not let it escalate into taking the process down.
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _hangDetector?.Dispose(); } catch { /* ignore */ }

        try { _services?.Dispose(); }
        catch { /* nothing useful left to do */ }

        // Last thing: from here on, a missing marker means this run ended properly.
        try { CrashReporter.EndSession(); } catch { /* ignore */ }

        try { _instanceMutex?.ReleaseMutex(); } catch { /* not owned */ }
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}

