using System.Globalization;

namespace Seein.Core.Apps;

/// <summary>
/// Stable colours for applications.  A curated categorical palette is used first because generated
/// hues tend to look alike at small sizes (a 8 px dot or a 4 px gantt bar); beyond the palette the
/// colours are darkened/lightened in rotation so two apps never share the exact same shade.
/// </summary>
public static class AppPalette
{
    // Tableau/Plotly style categorical palette, contrasting on both light and dark surfaces.
    private static readonly uint[] Palette =
    [
        0xFF1F77B4, 0xFFFF7F0E, 0xFF2CA02C, 0xFFD62728, 0xFF9467BD, 0xFF8C564B,
        0xFFE377C2, 0xFF17BECF, 0xFFBCBD22, 0xFF393B79, 0xFF637939, 0xFF8C6D31,
        0xFF843C39, 0xFF7B4173, 0xFF3182BD, 0xFF637CB3
    ];

    public static uint ColorAt(int index)
    {
        if (index < 0) index = 0;
        var color = Palette[index % Palette.Length];
        var round = index / Palette.Length;
        return round switch
        {
            0 => color,
            1 => Shade(color, 0.78),
            2 => Shade(color, 1.22),
            _ => Shade(color, round % 2 == 0 ? 0.62 : 1.38)
        };
    }

    private static uint Shade(uint color, double factor)
    {
        var red = (byte)Math.Clamp(Math.Round(((color >> 16) & 0xFF) * factor), 0, 255);
        var green = (byte)Math.Clamp(Math.Round(((color >> 8) & 0xFF) * factor), 0, 255);
        var blue = (byte)Math.Clamp(Math.Round((color & 0xFF) * factor), 0, 255);
        return 0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue;
    }

    public static string Format(uint color) =>
        string.Create(CultureInfo.InvariantCulture, $"#{color & 0x00FFFFFF:X6}");
}
