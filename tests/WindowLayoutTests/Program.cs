using System.Drawing;
using TextFixer;

var count = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; }
foreach (var dpi in new[] { 96, 144, 168, 192 })
foreach (var area in new[] { new Rectangle(0, 0, 1920, 1040), new Rectangle(-3840, 40, 3840, 2120), new Rectangle(1920, -100, 5120, 1400), new Rectangle(0, 0, 640, 480) })
{
    var (bounds, min) = WindowLayout.Initial(area, dpi);
    Check(area.Contains(bounds), "window remains inside working area");
    Check(bounds.Width >= min.Width && bounds.Height >= min.Height, "minimum respected");
    Check(Math.Abs(bounds.Left + bounds.Width / 2d - (area.Left + area.Width / 2d)) <= .5, "horizontally centred");
    Check(Math.Abs(bounds.Top + bounds.Height / 2d - (area.Top + area.Height / 2d)) <= .5, "vertically centred");
}
Check(WindowLayout.Initial(new Rectangle(0,0,1920,1040),96).Bounds.Size == new Size(960,832), "normal display fractions");
Check(WindowLayout.Initial(new Rectangle(0,0,3840,2120),168).Bounds.Size == new Size(1920,1696), "175 percent DPI does not scale fractions twice");
Check(WindowLayout.Initial(new Rectangle(0,0,7680,2120),96).Bounds.Width == 1400, "ultrawide width capped");
Check(WindowLayout.Initial(new Rectangle(0,0,3840,2076),168).Bounds.Size == new Size(1920,1661), "user screen uses 50 percent width and 80 percent height");
Console.WriteLine($"PASS: {count} screen-relative window layout checks.");
