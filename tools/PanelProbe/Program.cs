using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProjectSoundboard.App.Controls;

namespace PanelProbe;

/// <summary>
/// Drives <see cref="VirtualizingWrapPanel"/> through the bring-into-view path that killed
/// the app on large libraries: selecting an item that is far outside the viewport.
///
/// A stack overflow cannot be caught, so the harness proves itself by exit code — the
/// process either finishes and prints PASS, or Windows terminates it with 0xC00000FD.
/// </summary>
internal static class Program
{
    private const int ItemCount = 5000;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 1;

        var list = new ListBox
        {
            ItemsPanel = BuildPanelTemplate(),
            ItemTemplate = BuildItemTemplate(),
            ItemsSource = Enumerable.Range(0, ItemCount).Select(i => $"Sound {i:0000}").ToList()
        };

        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);

        var window = new Window
        {
            Title = "PanelProbe",
            Width = 900,
            Height = 600,
            // Kept off screen: this is a test rig, not something to look at.
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false,
            Content = list
        };

        window.Loaded += (_, _) =>
        {
            try
            {
                Run(list);
                Console.WriteLine();
                Console.WriteLine("PASS — no layout recursion");
                exitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"FAIL — {ex.GetType().Name}: {ex.Message}");
                exitCode = 1;
            }
            finally
            {
                app.Shutdown();
            }
        };

        window.Show();
        app.Run();

        return exitCode;
    }

    private static void Run(ListBox list)
    {
        // Long jumps in both directions are the case that matters: with everything already
        // on screen nothing ever asks to scroll, which is why small libraries never crashed.
        int[] jumps = [4999, 0, 2500, 4998, 7, 3333, 1, 4000, 12, 4999, 250, 4750];

        foreach (var index in jumps)
        {
            Select(list, index);
            Report(list, index);
        }

        // Then hammer it: alternating far ends, the pattern most likely to make a broken
        // MakeVisible oscillate instead of converge.
        for (var i = 0; i < 200; i++)
        {
            Select(list, i % 2 == 0 ? ItemCount - 1 - i : i);
        }

        Console.WriteLine("400 far-jump selections completed");

        // Keyboard navigation is the other half, and the more dangerous one: it runs through
        // ItemsControl.NavigateByLine, which realises the target and forces a layout pass
        // from inside one. That is where the nesting ran away.
        Console.WriteLine();
        Navigate(list, Key.Down, 60, "Down");
        Navigate(list, Key.Next, 60, "PageDown");
        Navigate(list, Key.Up, 60, "Up");
        Navigate(list, Key.Prior, 60, "PageUp");
        Navigate(list, Key.End, 5, "End");
        Navigate(list, Key.Home, 5, "Home");

        // Sweeping the width drags the layout across every column-count boundary, where the
        // extent changes, the scrollbar may appear or vanish, and a custom IScrollInfo can
        // oscillate instead of settling.
        Console.WriteLine();
        Sweep(list, 140d, 186d, "grid");

        // List view sets an absurd ItemWidth to force one column. That makes the reported
        // extent enormously wider than the viewport, which is its own way to upset a
        // ScrollViewer.
        Console.WriteLine();
        Sweep(list, 100000d, 48d, "list");
    }

    private static void Sweep(ListBox list, double itemWidth, double itemHeight, string label)
    {
        var window = Window.GetWindow(list)!;

        var factory = new FrameworkElementFactory(typeof(VirtualizingWrapPanel));
        factory.SetValue(VirtualizingWrapPanel.ItemWidthProperty, itemWidth);
        factory.SetValue(VirtualizingWrapPanel.ItemHeightProperty, itemHeight);
        list.ItemsPanel = new ItemsPanelTemplate(factory);
        list.UpdateLayout();
        Drain();

        for (var width = 320d; width <= 1400d; width += 7d)
        {
            window.Width = width;
            list.UpdateLayout();
            Drain();
        }

        for (var width = 1400d; width >= 320d; width -= 3d)
        {
            window.Width = width;
            list.UpdateLayout();
            Drain();
        }

        window.Width = 900;
        list.UpdateLayout();
        Drain();

        var scroll = FindScrollViewer(list);
        Console.WriteLine(
            $"{label,-5} width sweep completed  extent={scroll?.ExtentHeight,9:F0}  " +
            $"offset={scroll?.VerticalOffset,9:F1}");
    }

    private static void Navigate(ListBox list, Key key, int times, string label)
    {
        list.Focus();
        Drain();

        var source = PresentationSource.FromVisual(list);
        if (source is null)
        {
            Console.WriteLine($"{label}: no presentation source — SKIPPED");
            return;
        }

        for (var i = 0; i < times; i++)
        {
            // Raised on the element itself: in-process only, no OS-level synthetic input.
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            };

            list.RaiseEvent(args);
            list.UpdateLayout();
            Drain();
        }

        var scroll = FindScrollViewer(list);
        Console.WriteLine(
            $"{label,-9} x{times,-4} selected={list.SelectedIndex,5}  offset={scroll?.VerticalOffset,9:F1}");
    }

    private static void Select(ListBox list, int index)
    {
        list.SelectedIndex = index;
        list.ScrollIntoView(list.Items[index]);
        list.UpdateLayout();

        // ScrollIntoView defers through the dispatcher, so let those run too.
        Drain();
    }

    private static void Report(ListBox list, int index)
    {
        var scroll = FindScrollViewer(list);
        Console.WriteLine(
            $"index {index,4}  offset={scroll?.VerticalOffset,9:F1}  " +
            $"extent={scroll?.ExtentHeight,9:F0}  viewport={scroll?.ViewportHeight,6:F0}");
    }

    private static void Drain()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;

        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }

        return null;
    }

    private static ItemsPanelTemplate BuildPanelTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(VirtualizingWrapPanel));
        factory.SetValue(VirtualizingWrapPanel.ItemWidthProperty, 140d);
        factory.SetValue(VirtualizingWrapPanel.ItemHeightProperty, 186d);
        return new ItemsPanelTemplate(factory);
    }

    private static DataTemplate BuildItemTemplate()
    {
        // A nested element so focus lands *inside* the container, exactly as it does with
        // the real tiles — that is what the old lookup could never resolve.
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(4));
        border.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Gray);

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        border.AppendChild(text);

        return new DataTemplate { VisualTree = border };
    }
}
