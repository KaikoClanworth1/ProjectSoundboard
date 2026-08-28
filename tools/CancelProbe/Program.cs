using System.Windows;
using System.Windows.Controls;

namespace CancelProbe;

/// <summary>
/// What a dialog actually does when Cancel is pressed while work is still running.
///
/// The download window treats Cancel as "stop what you are doing", and only closes on the
/// second press. That is only true if returning early from the handler is enough to stop the
/// window closing by itself, and if it is not, everything the running download does afterwards
/// is happening to a window that has already gone.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var failures = 0;

        failures += DoesCancelCloseTheWindow(handled: false);
        failures += DoesCancelCloseTheWindow(handled: true);
        failures += AnsweringTwice();
        failures += SettingDialogResultAfterClose();
        failures += UsingADisposedTokenSource();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "No trap found."
            : $"{failures} trap(s) confirmed — both are guarded in the download window: the " +
              "result is only set while the window is still a dialog (Finish), and the token " +
              "source outlives the close (OnClosed).");

        return 0;
    }

    /// <summary>
    /// A Cancel button carries IsCancel, so WPF has its own opinion about what pressing it
    /// means. If that opinion wins, the window closes underneath the running download.
    /// </summary>
    private static int DoesCancelCloseTheWindow(bool handled)
    {
        var closed = false;

        var window = new Window { Width = 200, Height = 100, ShowInTaskbar = false, Left = -2000 };
        var cancel = new Button { Content = "Cancel", IsCancel = true };

        cancel.Click += (_, e) =>
        {
            // What the download window does: stop the work, leave the window open.
            e.Handled = handled;
        };

        var iClosedIt = false;

        window.Content = cancel;
        window.Closed += (_, _) => { if (!iClosedIt) closed = true; };

        // ShowDialog, not Show: IsCancel only has an opinion inside a dialog, which is what
        // the download window is.
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() =>
        {
            // Invoked the way a click invokes it. Raising the Click event directly is not the
            // same thing: what IsCancel means happens inside Button.OnClick, which only a real
            // press reaches.
            var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(cancel);
            var invoke = (System.Windows.Automation.Provider.IInvokeProvider)
                peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);

            invoke.Invoke();

            if (!window.IsVisible) return;

            iClosedIt = true;
            window.Close();
        }));

        window.ShowDialog();

        var label = handled ? "handler sets e.Handled" : "handler just returns";
        Console.WriteLine($"  Cancel pressed, {label,-24} -> window closed itself: {closed}");

        if (!closed) return 0;

        Console.WriteLine("        the window goes even though the handler meant to keep it open.");
        return handled ? 1 : 0;
    }

    /// <summary>
    /// A Cancel button whose own handler answers the dialog — which is what "Later" and
    /// "Skip this version" do on the update window, and what every ordinary Cancel does.
    ///
    /// The handler closes the window by setting the result. IsCancel then wants to set the
    /// result too, on a window that has just gone, and there is nowhere for that to be
    /// caught: it comes out of a click handler and ends the app.
    /// </summary>
    private static int AnsweringTwice()
    {
        Exception? escaped = null;

        var window = new Window { Width = 200, Height = 100, ShowInTaskbar = false, Left = -2000 };

        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() =>
        {
            // The first click answers the dialog, which closes it.
            window.DialogResult = false;

            // The second one arrives a moment later, at a window that has gone. This is an
            // impatient double-click on Later, and it is the whole of the crash.
            try { window.DialogResult = false; }
            catch (Exception ex) { escaped = ex; }

            if (window.IsVisible) window.Close();
        }));

        window.ShowDialog();

        Console.WriteLine($"  Answering a dialog a second time           -> " +
                          $"{(escaped is null ? "no exception" : escaped.GetType().Name)}");

        if (escaped is null) return 0;

        Console.WriteLine($"        \"{escaped.Message}\"");
        return 1;
    }

    /// <summary>
    /// What the download does when it finishes or is cancelled: report a result. Harmless,
    /// unless the window has already closed.
    /// </summary>
    private static int SettingDialogResultAfterClose()
    {
        var window = new Window { Width = 200, Height = 100, ShowInTaskbar = false, Left = -2000 };

        window.Show();
        Pump();
        window.Close();
        Pump();

        try
        {
            window.DialogResult = true;
            Console.WriteLine("  DialogResult after the window closed        -> allowed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  DialogResult after the window closed        -> {ex.GetType().Name}");
            Console.WriteLine($"        \"{ex.Message}\"");
            return 1;
        }
    }

    /// <summary>
    /// Closing disposes the source that the running download is still holding a token from.
    /// </summary>
    private static int UsingADisposedTokenSource()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        source.Dispose();

        try
        {
            _ = source.Token;
            Console.WriteLine("  Token from a disposed source               -> allowed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Token from a disposed source               -> {ex.GetType().Name}");
            return 1;
        }
    }

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();

        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));

        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
