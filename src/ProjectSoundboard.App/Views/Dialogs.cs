using System.Windows;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Views;

internal static class Dialogs
{
    /// <summary>
    /// Answer a dialog and close it — once, and only while there is still a dialog to answer.
    ///
    /// Setting the result a second time throws, and a button's Click handler is not somewhere
    /// an exception can be caught: it goes straight past everything and ends the app. Two
    /// ordinary things lead there. A second click landing while the first is still closing
    /// the window, which is what an impatient double-click on Later or Cancel is. And a
    /// button carrying IsCancel, where WPF wants to answer the dialog itself after the
    /// handler has already answered it.
    ///
    /// Neither is worth losing the app over: the dialog is already going, and the only
    /// question is whether it goes quietly.
    /// </summary>
    public static void Answer(this Window window, bool result)
    {
        try
        {
            window.DialogResult = result;
        }
        catch (InvalidOperationException ex)
        {
            // Already answered, already closed, or never shown as a dialog at all.
            Log.Debug($"{window.GetType().Name} was already closed: {ex.Message}");
        }
    }
}
