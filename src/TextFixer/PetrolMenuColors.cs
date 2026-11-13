namespace TextFixer;

internal sealed class PetrolMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.FromArgb(0x0d, 0x2c, 0x3b);
    public override Color MenuBorder => Color.FromArgb(0x26, 0x48, 0x59);
    public override Color MenuItemSelected => Color.FromArgb(0x26, 0x48, 0x59);
    public override Color MenuItemBorder => Color.FromArgb(0xf5, 0xcc, 0x57);
    public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
    public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
    public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
    public override Color SeparatorDark => MenuBorder;
    public override Color SeparatorLight => MenuBorder;
}
