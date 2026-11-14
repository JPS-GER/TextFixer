using System.Runtime.InteropServices;

namespace TextFixer;

/// <summary>A native notice using the same palette as the review window and tray.</summary>
internal sealed class RunningNotice : Form
{
    private readonly Icon _icon = Branding.CreateWindowIcon();

    internal RunningNotice(string shortcut)
    {
        Text = "TextFixer";
        Icon = _icon;
        BackColor = Color.FromArgb(0x0a, 0x25, 0x32);
        ForeColor = PetrolMenuColors.Text;
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 172);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(22, 18, 22, 18)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        var message = new Label
        {
            Dock = DockStyle.Fill,
            Text = $"TextFixer is already running.\nUse {shortcut} or its tray icon.",
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 0, 14)
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
            Size = new Size(96, 36),
            Margin = Padding.Empty,
            FlatStyle = FlatStyle.Flat,
            BackColor = PetrolMenuColors.TextHover,
            ForeColor = BackColor,
            UseVisualStyleBackColor = false
        };
        ok.FlatAppearance.BorderColor = PetrolMenuColors.TextHover;
        ok.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xff, 0xdc, 0x75);
        ok.FlatAppearance.MouseDownBackColor = Color.FromArgb(0xdb, 0xb4, 0x43);
        layout.Controls.Add(message, 0, 0);
        layout.Controls.Add(ok, 0, 1);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = ok;
        Disposed += (_, _) => _icon.Dispose();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var dark = 1;
            if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
            var caption = ColorTranslator.ToWin32(PetrolMenuColors.Background);
            var text = ColorTranslator.ToWin32(PetrolMenuColors.Text);
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
