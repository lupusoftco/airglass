using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace AirGlass.Support;

public static class QrCodeImage
{
    /// <summary>
    /// Renders <paramref name="text"/> as a black-on-white QR code, one pixel
    /// per module and no quiet zone (the white card provides it). Scale it up
    /// with nearest-neighbor sampling to keep the edges crisp.
    /// </summary>
    public static BitmapSource Create(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(1, false);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
