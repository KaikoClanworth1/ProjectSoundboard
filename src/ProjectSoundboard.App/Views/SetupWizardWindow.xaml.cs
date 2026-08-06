using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.App.ViewModels;

namespace ProjectSoundboard.App.Views;

public partial class SetupWizardWindow : Window
{
    private readonly SetupWizardViewModel _viewModel;
    private readonly DispatcherTimer _meterTimer;

    public SetupWizardWindow()
    {
        InitializeComponent();

        _viewModel = new SetupWizardViewModel(AppServices.Current);
        DataContext = _viewModel;

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _meterTimer.Tick += (_, _) =>
        {
            if (_viewModel.Step == WizardStep.Test) _viewModel.UpdateMeters();
        };
        _meterTimer.Start();

        Closed += (_, _) => _meterTimer.Stop();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsLastStep)
        {
            _viewModel.Commit();
            AppServices.Current.Library.StartWatching();
            DialogResult = true;
            return;
        }

        _viewModel.Next();
    }

    private void OnBack(object sender, RoutedEventArgs e) => _viewModel.Back();

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Leave setup?\n\n" +
            "Nothing is saved yet, and Project Soundboard needs at least a sound output " +
            "before it can do anything useful.",
            "Cancel setup", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        AppServices.Current.Engine.StopAll(0);
        DialogResult = false;
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
