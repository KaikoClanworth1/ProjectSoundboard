using System.Windows;
using System.Windows.Controls;
using ProjectSoundboard.App.ViewModels;
using ProjectSoundboard.Core.Library;

namespace ProjectSoundboard.App.Views;

public partial class PropertiesPanel : UserControl
{
    public PropertiesPanel()
    {
        InitializeComponent();
        Waveform.Seeked += OnWaveformSeeked;
    }

    private PropertiesViewModel? ViewModel => DataContext as PropertiesViewModel;

    private void OnWaveformSeeked(object? sender, double fraction) => ViewModel?.SeekTo(fraction);

    // Dropping an image straight onto the panel is the fastest way to give a sound artwork.
    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);

        var accepts = ViewModel?.Sound is not null && IsImageDrop(e);
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);

        if (ViewModel?.Sound is null) return;
        if (!IsImageDrop(e)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var image = paths.FirstOrDefault(ImageStore.IsSupportedImage);
        if (image is null) return;

        ViewModel.SetImageFromFile(image);

        // Stop the window-level handler treating this as an audio import.
        e.Handled = true;
    }

    private static bool IsImageDrop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        return e.Data.GetData(DataFormats.FileDrop) is string[] paths
               && paths.Any(ImageStore.IsSupportedImage);
    }
}
