using System.Windows;

namespace ProjectSoundboard.App.Views;

/// <summary>Small themed replacement for the input box WPF does not ship with.</summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string label, string? initialValue = null)
    {
        InitializeComponent();

        TitleText.Text = title;
        LabelText.Text = label;
        Input.Text = initialValue ?? string.Empty;

        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Value => Input.Text;

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
