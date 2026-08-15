using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoEditor.App.Ui;

/// <summary>Shared conversions between raw BGRA frames and WPF bitmaps.</summary>
public static class FrameBitmaps
{
    /// <summary>BGRA bytes → frozen bitmap, safe to hand to any thread.</summary>
    public static BitmapSource CreateFrozen(byte[] bgra, int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }
}
