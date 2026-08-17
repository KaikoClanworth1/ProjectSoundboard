using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace BindingProbe;

/// <summary>
/// Reproduces the device-selection bug in isolation.
///
/// The audio and microphone pages hold their device list in an ObservableCollection bound to
/// a combo box, with SelectedItem bound to a property whose change handler saves the chosen
/// device and reopens audio on it. Refreshing that list cleared the collection first and only
/// then set the "I am loading, ignore changes" guard.
///
/// This shows what that ordering costs: clearing a bound collection makes WPF write the
/// selection back as null, which runs the change handler for real.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var failures = 0;

        Console.WriteLine("Refreshing a device list while it is bound to a combo box.");
        Console.WriteLine();

        var broken = Run(guardBeforeClear: false);
        Console.WriteLine($"  guard set AFTER the clear  (the old code) : saved device = {Describe(broken)}");

        var fixedResult = Run(guardBeforeClear: true);
        Console.WriteLine($"  guard set BEFORE the clear (the fix)      : saved device = {Describe(fixedResult)}");

        Console.WriteLine();

        if (broken is not null)
        {
            Console.WriteLine("FAIL — the old ordering was expected to lose the device but did not.");
            failures++;
        }

        if (fixedResult != "microphone-2")
        {
            Console.WriteLine("FAIL — the fix was expected to keep 'microphone-2'.");
            failures++;
        }

        if (failures == 0)
            Console.WriteLine("PASS — clearing a bound list wipes the saved device, and guarding it first prevents that.");

        app.Shutdown();
        return failures == 0 ? 0 : 1;
    }

    private static string Describe(string? value) => value is null ? "(none — lost)" : $"'{value}'";

    /// <summary>Returns what ended up saved as the chosen device.</summary>
    private static string? Run(bool guardBeforeClear)
    {
        var model = new DevicePage { SavedDeviceId = "microphone-2" };

        var combo = new ComboBox { ItemsSource = model.Devices };
        combo.SetBinding(Selector.SelectedItemProperty,
            new Binding(nameof(DevicePage.Selected)) { Source = model, Mode = BindingMode.TwoWay });

        // A window so the binding is live, exactly as it is on the real pages.
        var window = new Window { Width = 200, Height = 100, Left = -4000, Top = -4000, Content = combo };
        window.Show();

        model.Devices.Add("microphone-1");
        model.Devices.Add("microphone-2");
        model.Selected = "microphone-2";
        Drain();

        model.Refresh(guardBeforeClear);
        Drain();

        window.Close();
        return model.SavedDeviceId;
    }

    private static void Drain()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>The shape of the real pages, reduced to the part that matters.</summary>
    private sealed class DevicePage : INotifyPropertyChanged
    {
        private bool _loading;
        private string? _selected;

        public ObservableCollection<string> Devices { get; } = new();

        public string? SavedDeviceId { get; set; }

        public string? Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
                OnSelectedChanged();
            }
        }

        /// <summary>Stands in for saving the device and reopening audio on it.</summary>
        private void OnSelectedChanged()
        {
            if (_loading) return;
            SavedDeviceId = Selected;
        }

        public void Refresh(bool guardBeforeClear)
        {
            if (guardBeforeClear) _loading = true;

            Devices.Clear();
            Devices.Add("microphone-1");
            Devices.Add("microphone-2");

            if (!guardBeforeClear) _loading = true;

            Selected = Devices.FirstOrDefault(d => d == SavedDeviceId) ?? Devices.FirstOrDefault();

            _loading = false;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
