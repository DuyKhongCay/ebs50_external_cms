using SkiaSharp;

namespace ebs50_backend.Services.Rendering;

/// <summary>
/// Defines the 4-color palette (White, Black, Red, Yellow) supported by ESL e-paper displays.
/// </summary>
public static class EslColorPalette
{
    // NOTE: Strict 4-color E-Paper values for SE420RY variant
    public static readonly SKColor White = new(255, 255, 255);
    public static readonly SKColor Black = new(0, 0, 0);
    public static readonly SKColor Red = new(220, 20, 20);      // High-contrast e-ink red
    public static readonly SKColor Yellow = new(255, 200, 0);    // High-contrast e-ink yellow

    private static readonly SKColor[] Palette = [White, Black, Red, Yellow];

    /// <summary>
    /// Resolves an SKColor from a theme color name.
    /// </summary>
    public static SKColor ResolveColor(string? colorName) =>
        colorName?.Trim().ToLowerInvariant() switch
        {
            "red" or "do" or "đỏ" => Red,
            "yellow" or "vang" or "vàng" => Yellow,
            "white" or "trang" or "trắng" => White,
            _ => Black
        };

    /// <summary>
    /// Snaps an arbitrary RGB color to the closest color in the 4-color e-paper palette.
    /// Uses Euclidean distance in RGB color space.
    /// </summary>
    public static SKColor SnapToClosestPaletteColor(SKColor color)
    {
        // Treat fully transparent pixels as white background
        if (color.Alpha < 64)
        {
            return White;
        }

        SKColor bestMatch = White;
        int minDistanceSq = int.MaxValue;

        foreach (var p in Palette)
        {
            int dr = color.Red - p.Red;
            int dg = color.Green - p.Green;
            int db = color.Blue - p.Blue;
            int distanceSq = (dr * dr) + (dg * dg) + (db * db);

            if (distanceSq < minDistanceSq)
            {
                minDistanceSq = distanceSq;
                bestMatch = p;
            }
        }

        return bestMatch;
    }
}

