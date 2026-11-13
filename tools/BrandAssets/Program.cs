using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Buffers.Binary;

// Development-only asset generator. Generated assets are checked in; app builds do not run it.
try
{
var root = Directory.GetCurrentDirectory();
var assets = Path.Combine(root, "src/TextFixer/Assets");
var web = Path.Combine(root, "src/TextFixer/Web");
var extension = Path.Combine(root, "chrome-extension/icons");
Directory.CreateDirectory(assets); Directory.CreateDirectory(extension);
var forest = ColorTranslator.FromHtml("#0A2532");
var teal = ColorTranslator.FromHtml("#F5CC57");
PointF[] t = [new(20,8),new(100,8),new(92,26),new(60,26),new(42,80),new(22,80),new(40,26),new(12,26)];
PointF[] f = [new(63,34),new(90,34),new(84,50),new(73,50),new(63,80),new(47,80)];
string Points(PointF[] points) => string.Join(" ", points.Select(p => p.X.ToString(CultureInfo.InvariantCulture)+","+p.Y.ToString(CultureInfo.InvariantCulture)));
string Svg(string first, string second, bool background = false) =>
    "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 112 112\" role=\"img\" aria-label=\"TextFixer\">"+
    (background ? "<rect width=\"112\" height=\"112\" rx=\"24\" fill=\"#0A2532\"/>" : "")+
    "<g transform=\"translate(0 12)\"><polygon fill=\""+first+"\" points=\""+Points(t)+"\"/><polygon fill=\""+second+"\" points=\""+Points(f)+"\"/></g></svg>\n";
File.WriteAllText(Path.Combine(web,"logo.svg"),Svg("#E8EEF1","#F5CC57"),new UTF8Encoding(false));
File.WriteAllText(Path.Combine(assets,"app.svg"),Svg("#FFFFFF","#FFFFFF",true),new UTF8Encoding(false));
File.WriteAllText(Path.Combine(assets,"tray-dark.svg"),Svg("#0A2532","#0A2532"),new UTF8Encoding(false));
File.WriteAllText(Path.Combine(assets,"tray-light.svg"),Svg("#FFFFFF","#FFFFFF"),new UTF8Encoding(false));

Bitmap Render(int size,string kind)
{
    using var large=new Bitmap(size*4,size*4,PixelFormat.Format32bppArgb);
    using(var graphics=Graphics.FromImage(large))
    {
        graphics.Clear(Color.Transparent);graphics.SmoothingMode=SmoothingMode.AntiAlias;
        graphics.ScaleTransform(size*4/112f,size*4/112f);
        if(kind=="app")
        {
            using var path=new GraphicsPath();const float diameter=48;
            path.AddArc(0,0,diameter,diameter,180,90);path.AddArc(112-diameter,0,diameter,diameter,270,90);
            path.AddArc(112-diameter,112-diameter,diameter,diameter,0,90);path.AddArc(0,112-diameter,diameter,diameter,90,90);path.CloseFigure();
            using var background=new SolidBrush(forest);graphics.FillPath(background,path);
        }
        graphics.TranslateTransform(0,12);
        var first=kind is "app" or "tray-light"?Color.White:forest;
        var second=kind=="color"?teal:first;
        using var brushT=new SolidBrush(first);using var brushF=new SolidBrush(second);
        graphics.FillPolygon(brushT,t);graphics.FillPolygon(brushF,f);
    }
    var small=new Bitmap(size,size,PixelFormat.Format32bppArgb);
    using(var graphics=Graphics.FromImage(small))
    {
        graphics.CompositingMode=CompositingMode.SourceCopy;
        graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode=PixelOffsetMode.HighQuality;
        graphics.DrawImage(large,new Rectangle(0,0,size,size));
    }
    return small;
}

void WriteIcon(string path,string kind)
{
    int[] sizes=[16,20,24,32,40,48,64,128,256];
    var images=sizes.Select(size=>{using var bitmap=Render(size,kind);using var stream=new MemoryStream();bitmap.Save(stream,ImageFormat.Png);return stream.ToArray();}).ToArray();
    using var output=new BinaryWriter(File.Create(path));output.Write((ushort)0);output.Write((ushort)1);output.Write((ushort)sizes.Length);
    var offset=6+16*sizes.Length;
    for(var i=0;i<sizes.Length;i++)
    {
        output.Write((byte)(sizes[i]==256?0:sizes[i]));output.Write((byte)(sizes[i]==256?0:sizes[i]));
        output.Write((byte)0);output.Write((byte)0);output.Write((ushort)1);output.Write((ushort)32);
        output.Write(images[i].Length);output.Write(offset);offset+=images[i].Length;
    }
    foreach(var bytes in images)output.Write(bytes);
}
WriteIcon(Path.Combine(assets,"TextFixer.ico"),"app");
WriteIcon(Path.Combine(assets,"tray-dark.ico"),"tray-dark");
WriteIcon(Path.Combine(assets,"tray-light.ico"),"tray-light");
foreach(var size in new[]{16,32,48,128})
{
    using var bitmap=Render(size,"app");bitmap.Save(Path.Combine(extension,$"icon-{size}.png"),ImageFormat.Png);
}
using(var preview=new Bitmap(760,300))
using(var graphics=Graphics.FromImage(preview))
using(var font=new Font("Segoe UI",11))
{
    graphics.Clear(Color.FromArgb(247,248,245));
    graphics.DrawString("Approved TF ligature: production assets",font,Brushes.DarkSlateGray,20,15);
    using(var color=Render(150,"color"))graphics.DrawImageUnscaled(color,25,55);
    using(var app=Render(150,"app"))graphics.DrawImageUnscaled(app,200,55);
    graphics.FillRectangle(Brushes.White,385,70,330,60);graphics.FillRectangle(Brushes.Black,385,145,330,60);
    var x=400;
    foreach(var size in new[]{16,20,24,32,48})
    {
        using var dark=Render(size,"tray-dark");using var light=Render(size,"tray-light");
        graphics.DrawImageUnscaled(dark,x,90-size/2);graphics.DrawImageUnscaled(light,x,175-size/2);
        graphics.DrawString(size.ToString(),font,Brushes.DarkSlateGray,x,220);x+=60;
    }
    preview.Save(Path.Combine(root,"design/logo-concepts/textfixer-02-production.png"),ImageFormat.Png);
}
foreach(var file in new[]{"TextFixer.ico","tray-dark.ico","tray-light.ico"})
foreach(var size in new[]{16,20,24,32,48})
{
    using var icon=new Icon(Path.Combine(assets,file),size,size);
    if(icon.Width!=size||icon.Height!=size)throw new Exception("Invalid ICO frame: "+file+" "+size);
}
foreach(var file in new[]{"TextFixer.ico","tray-dark.ico","tray-light.ico"})
{
    var bytes=File.ReadAllBytes(Path.Combine(assets,file));
    var count=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4,2));
    for(var i=0;i<count;i++)
    {
        var entry=6+i*16;
        var width=bytes[entry]==0?256:bytes[entry];
        var length=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(entry+8,4));
        var offset=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(entry+12,4));
        if(offset<6+count*16||length<24||offset+length>bytes.Length||BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset+16,4))!=width)
            throw new Exception("Invalid encoded ICO frame: "+file+" "+width);
    }
}
Console.WriteLine("Generated SVGs, multi-resolution app/tray ICOs, Chrome PNGs, and production preview. ICO frames verified.");
}
catch(Exception error)
{
    Console.Error.WriteLine("Brand asset generation failed: "+error.Message);
    Environment.ExitCode=1;
}
