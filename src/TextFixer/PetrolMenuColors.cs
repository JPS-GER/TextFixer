namespace TextFixer;

internal sealed class PetrolMenuColors : ProfessionalColorTable
{
    public static readonly Color Background = Color.FromArgb(0x0d, 0x2c, 0x3b);
    public static readonly Color Hover = Color.FromArgb(0x16, 0x3a, 0x4c);
    public static readonly Color HoverEdge = Color.FromArgb(0x5e, 0x64, 0x45);
    public static readonly Color Edge = Color.FromArgb(0x26, 0x48, 0x59);
    public static readonly Color Text = Color.FromArgb(0xe8, 0xee, 0xf1);
    public static readonly Color TextHover = Color.FromArgb(0xf5, 0xcc, 0x57);
    public static readonly Color TextDisabled = Color.FromArgb(0x7f, 0x95, 0xa0);

    public PetrolMenuColors() => UseSystemColors = false;

    public override Color ToolStripDropDownBackground => Background;
    public override Color MenuBorder => Edge;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemBorder => HoverEdge;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemPressedGradientBegin => Hover;
    public override Color MenuItemPressedGradientMiddle => Hover;
    public override Color MenuItemPressedGradientEnd => Hover;
    public override Color ImageMarginGradientBegin => Background;
    public override Color ImageMarginGradientMiddle => Background;
    public override Color ImageMarginGradientEnd => Background;
    public override Color SeparatorDark => Edge;
    public override Color SeparatorLight => Edge;
}

/// <summary>Paints tray menu items in the app palette regardless of the Windows highlight color.</summary>
internal sealed class PetrolMenuRenderer() : ToolStripProfessionalRenderer(new PetrolMenuColors())
{
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var bounds = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height - 1);
        using var fill = new SolidBrush(PetrolMenuColors.Background);
        e.Graphics.FillRectangle(fill, new Rectangle(Point.Empty, e.Item.Size));
        if (!e.Item.Selected || !e.Item.Enabled) return;
        using var hover = new SolidBrush(PetrolMenuColors.Hover);
        using var edge = new Pen(PetrolMenuColors.HoverEdge);
        e.Graphics.FillRectangle(hover, bounds);
        e.Graphics.DrawRectangle(edge, bounds);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? PetrolMenuColors.TextDisabled
            : e.Item.Selected ? PetrolMenuColors.TextHover
            : PetrolMenuColors.Text;
        base.OnRenderItemText(e);
    }
}
