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
Check(WindowLayout.Initial(new Rectangle(0,0,1920,1040),96).Bounds.Size == new Size(1145,832), "start width keeps the review tabs on one line at 15 px");
Check(WindowLayout.Initial(new Rectangle(0,0,2560,1400),96).Bounds.Width == 1280, "half the screen when that is wider than the tab line");
Check(WindowLayout.Initial(new Rectangle(0,0,3840,2120),168).Bounds.Size == new Size(2004,1696), "175 percent DPI scales the tab line width once");
Check(WindowLayout.Initial(new Rectangle(0,0,7680,2120),96).Bounds.Width == 1400, "ultrawide width capped");
Check(WindowLayout.Initial(new Rectangle(0,0,3840,2076),168).Bounds.Size == new Size(2004,1661), "user screen opens at the tab line width and 80 percent height");
Check(WindowLayout.MinimumWidth(1274,168,new Rectangle(0,0,3840,2076)) == 2230, "18 px minimum on the user screen");
Check(WindowLayout.MinimumWidth(1274,96,new Rectangle(0,0,1024,768)) == 1024, "minimum never exceeds the screen");
Console.WriteLine($"PASS: {count} screen-relative window layout checks.");
