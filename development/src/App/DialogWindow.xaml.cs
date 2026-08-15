using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoEditor.App.Ui;

namespace VideoEditor.App;

/// <summary>
/// The app's single dialog window: icon + message + optional details + a
/// caller-defined button row. Every confirmation, warning and error in the
/// editor goes through here, so they all look the same and match the dark
/// theme (unlike the native MessageBox).
/// </summary>
public partial class DialogWindow : Window
{
    private readonly DialogOptions _options;

    public DialogWindow(DialogOptions options)
    {
        InitializeComponent();
        _options = options;

        Title = options.Title;
        MessageText.Text = options.Message;

        if (!string.IsNullOrWhiteSpace(options.Details))
        {
            DetailsText.Text = options.Details;
            DetailsBox.Visibility = Visibility.Visible;
        }

        var (glyph, color) = ToneVisual(options.Tone);
        IconText.Text = glyph;
        IconText.Foreground = new SolidColorBrush(color);

        BuildButtons();
    }

    /// <summary>The chosen button's result, or the dismiss result.</summary>
    public string? Result { get; private set; }

    private void BuildButtons()
    {
        foreach (var definition in _options.Buttons)
        {
            var button = new Button
            {
                Content = definition.Text,
                Style = (Style)FindResource("ToolButton"),
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 88,
                IsDefault = definition.IsPrimary,
                Tag = definition.Result
            };

            if (definition.IsDestructive)
            {
                button.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x1E, 0x22));
                button.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9E, 0x9E));
            }
            else if (definition.IsPrimary)
            {
                button.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x33, 0x21));
                button.Foreground = new SolidColorBrush(Color.FromRgb(0x9B, 0xE2, 0x8F));
                button.FontWeight = FontWeights.SemiBold;
            }

            button.Click += Button_Click;
            ButtonRow.Children.Add(button);
            if (definition.IsPrimary) _ = button.Focus();
        }
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        Result = (sender as Button)?.Tag as string;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Result = _options.DismissResult;
        Close();
        e.Handled = true;
    }

    /// <summary>Segoe MDL2 glyph + accent per tone (escapes, never raw PUA characters).</summary>
    private static (string Glyph, Color Color) ToneVisual(DialogTone tone) => tone switch
    {
        DialogTone.Question => ("\uE9CE", Color.FromRgb(0x8F, 0xB8, 0xF0)),
        DialogTone.Warning => ("\uE7BA", Color.FromRgb(0xFF, 0xD5, 0x4F)),
        DialogTone.Error => ("\uE783", Color.FromRgb(0xE5, 0x73, 0x73)),
        DialogTone.Success => ("\uE73E", Color.FromRgb(0x6C, 0xCB, 0x5F)),
        _ => ("\uE946", Color.FromRgb(0x8F, 0x96, 0xA3))
    };
}
