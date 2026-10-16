using System.Windows;
using System.Windows.Media;

namespace AirGlass.Views;

/// <summary>
/// Colors from the AirGlass design (the same tokens as the macOS app),
/// published as application resources for DynamicResource lookups.
/// </summary>
public static class Theme
{
    public static void Apply(bool light)
    {
        Set("TextBrush", light ? 0x1D1D1F : 0xF5F5F7);
        Set("SecondaryTextBrush", light ? 0x6E6E73 : 0xA1A1A6);
        Set("AccentBrush", light ? 0x5B6EF5 : 0x7383FF);
        Set("AccentFillBrush", light ? 0x5B6EF5 : 0x7383FF, light ? 0.12 : 0.20);
        Set("GreenBrush", light ? 0x28A745 : 0x30D158);
        Set("OrangeBrush", light ? 0xC96A00 : 0xFF9F0A);
        Set("OrangeFillBrush", light ? 0xFF9500 : 0xFF9F0A, light ? 0.14 : 0.18);
        Set("RedBrush", light ? 0xE5352B : 0xFF453A);

        // Inset panel behind the QR code and the device card.
        Set("CardBrush", light ? 0x000000 : 0xFFFFFF, light ? 0.04 : 0.06);
        // Buttons and icon tiles.
        Set("ControlBrush", light ? 0x000000 : 0xFFFFFF, light ? 0.06 : 0.10);
        Set("SegmentTrackBrush", light ? 0x000000 : 0xFFFFFF, light ? 0.07 : 0.08);
        Set("SegmentSelectedBrush", 0xFFFFFF, light ? 1.0 : 0.22);
        Set("SeparatorBrush", light ? 0x3C3C43 : 0xFFFFFF, light ? 0.16 : 0.11);
        Set("MenuHoverBrush", light ? 0x000000 : 0xFFFFFF, light ? 0.06 : 0.10);

        // The popover panel. The macOS window is translucent glass; here it
        // is nearly opaque, since a layered WPF window cannot blur behind it.
        Set("PanelBrush", light ? 0xF6F6F8 : 0x26262C, 0.98);
        Set("PanelBorderBrush", light ? 0x000000 : 0xFFFFFF, light ? 0.12 : 0.13);
        Set("ShadowColor", 0x000000, light ? 0.22 : 0.55, asColor: true);
    }

    private static void Set(string key, uint rgb, double alpha = 1, bool asColor = false)
    {
        var color = Color.FromArgb(
            (byte)System.Math.Round(alpha * 255),
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF));
        if (asColor)
        {
            Application.Current.Resources[key] = color;
            return;
        }
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }
}
