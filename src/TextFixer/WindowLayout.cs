using System.Drawing;

namespace TextFixer;

internal static class WindowLayout
{
    internal static (Rectangle Bounds, Size Minimum) Initial(Rectangle area, int dpi)
    {
        var scale = Math.Max(96, dpi) / 96d;
        var minimum = new Size(Math.Min(area.Width, (int)Math.Round(740 * scale)),
            Math.Min(area.Height, (int)Math.Round(600 * scale)));
        var width = Math.Clamp((int)Math.Round(area.Width * .50), minimum.Width,
            Math.Max(minimum.Width, Math.Min(area.Width, (int)Math.Round(1400 * scale))));
        var height = Math.Clamp((int)Math.Round(area.Height * .80), minimum.Height, area.Height);
        return (new Rectangle(area.Left + (area.Width - width) / 2,
            area.Top + (area.Height - height) / 2, width, height), minimum);
    }
}
