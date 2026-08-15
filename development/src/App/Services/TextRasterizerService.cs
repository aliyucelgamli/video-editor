using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoEditor.Domain;
using VideoEditor.MediaEngine.Frames;

namespace VideoEditor.App.Services;

/// <summary>
/// Renders text styles with WPF's text stack (FormattedText → RenderTargetBitmap)
/// into the media engine's raster cache. Runs on the UI thread; the compositor
/// then layers the cached frames from any thread, for preview and export alike.
/// Text is drawn centered at project scale — position/scale come from the
/// event's Transform, so the gizmo editor works on titles too.
/// </summary>
public class TextRasterizerService
{
    private readonly TextRasterCache _cache;

    public TextRasterizerService(TextRasterCache cache) => _cache = cache;

    /// <summary>Rasterizes the style at the given canvas size unless already cached.</summary>
    public void EnsureRendered(TextStyle style, int width, int height, int projectWidth)
    {
        width -= width % 2;
        height -= height % 2;
        if (width < 2 || height < 2) return;
        if (_cache.TryGetShared(style, width, height) != null) return;

        try
        {
            _cache.Store(style, width, height, Render(style, width, height, projectWidth));
        }
        catch
        {
            // A broken font name must never take the app down; the layer is skipped.
        }
    }

    private static RawFrame Render(TextStyle style, int width, int height, int projectWidth)
    {
        var scale = projectWidth > 0 ? (double)width / projectWidth : 1;
        var typeface = new Typeface(
            new FontFamily(string.IsNullOrWhiteSpace(style.FontFamily) ? "Segoe UI" : style.FontFamily),
            style.Italic ? FontStyles.Italic : FontStyles.Normal,
            style.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);

        var text = new FormattedText(
            style.Content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, Math.Max(1, style.FontSize * scale), BrushFromHex(style.Color),
            pixelsPerDip: 1.0)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = width
        };

        var visual = new DrawingVisual();
        // Ideal formatting scales glyph outlines instead of snapping them to a
        // pixel grid sized for the (small) preview canvas, and grayscale
        // antialiasing avoids ClearType color fringes on transparent pixels.
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);

        using (var context = visual.RenderOpen())
        {
            var origin = new Point(0, Math.Max(0, (height - text.Height) / 2));
            if (style.OutlineWidth > 0.01)
            {
                var pen = new Pen(BrushFromHex(style.OutlineColor), style.OutlineWidth * scale)
                {
                    LineJoin = PenLineJoin.Round
                };
                context.DrawGeometry(null, pen, text.BuildGeometry(origin));
            }
            context.DrawText(text, origin);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        UnPremultiply(pixels);
        return new RawFrame(pixels, width, height);
    }

    /// <summary>Pbgra32 is premultiplied; the compositor blends straight BGRA.</summary>
    private static void UnPremultiply(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 0 || alpha == 255) continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / alpha);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / alpha);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / alpha);
        }
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.White;
        }
    }
}
