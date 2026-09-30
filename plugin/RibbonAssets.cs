using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WEAM.Revit.AI;

internal static class RibbonAssets
{
    public static BitmapImage Icon(int pixels)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WEAM.Revit.AI.Assets.weam-ai.png")
            ?? throw new InvalidOperationException("Embedded WEAM icon is missing.");
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = pixels;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static DrawingImage AutoConfirmIcon(int pixels)
    {
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(15, 91, 121)), pixels == 16 ? 1.2 : 1.5);
        var tile = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(19, 124, 154)), outline,
            new RectangleGeometry(new Rect(1, 1, 30, 30), 6, 6));
        var check = new GeometryDrawing(null,
            new Pen(Brushes.White, 3.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round },
            Geometry.Parse("M 7,16 L 13,22 L 25,10"));
        var group = new DrawingGroup();
        group.Children.Add(tile);
        group.Children.Add(check);
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
