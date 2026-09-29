using System.Reflection;
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
}
