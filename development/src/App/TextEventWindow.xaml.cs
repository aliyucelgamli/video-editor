using System.Globalization;
using System.Windows;
using System.Windows.Media;
using VideoEditor.Domain;

namespace VideoEditor.App;

/// <summary>
/// Add/edit dialog for text (title) events: content, font, size, bold/italic,
/// fill and outline. Confirming exposes the resulting <see cref="TextStyle"/>.
/// </summary>
public partial class TextEventWindow : Window
{
    public TextStyle TextStyle { get; private set; }

    public TextEventWindow(TextStyle? existing = null)
    {
        InitializeComponent();
        TextStyle = existing?.Clone() ?? new TextStyle();

        FontBox.ItemsSource = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ContentBox.Text = TextStyle.Content;
        FontBox.Text = TextStyle.FontFamily;
        SizeBox.Text = TextStyle.FontSize.ToString("0.##", CultureInfo.InvariantCulture);
        BoldCheck.IsChecked = TextStyle.Bold;
        ItalicCheck.IsChecked = TextStyle.Italic;
        ColorBox.Text = TextStyle.Color;
        OutlineColorBox.Text = TextStyle.OutlineColor;
        OutlineWidthBox.Text = TextStyle.OutlineWidth.ToString("0.##", CultureInfo.InvariantCulture);

        ContentBox.Focus();
        ContentBox.SelectAll();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ContentBox.Text))
        {
            MessageBox.Show("Please enter some text.", "Text",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TextStyle = new TextStyle
        {
            Content = ContentBox.Text,
            FontFamily = string.IsNullOrWhiteSpace(FontBox.Text) ? "Segoe UI" : FontBox.Text,
            FontSize = ParseDouble(SizeBox.Text, TextStyle.FontSize, 8, 800),
            Bold = BoldCheck.IsChecked == true,
            Italic = ItalicCheck.IsChecked == true,
            Color = NormalizeColor(ColorBox.Text, TextStyle.Color),
            OutlineColor = NormalizeColor(OutlineColorBox.Text, TextStyle.OutlineColor),
            OutlineWidth = ParseDouble(OutlineWidthBox.Text, TextStyle.OutlineWidth, 0, 60)
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static double ParseDouble(string text, double fallback, double min, double max) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static string NormalizeColor(string text, string fallback)
    {
        try
        {
            ColorConverter.ConvertFromString(text);
            return text;
        }
        catch
        {
            return fallback;
        }
    }
}
