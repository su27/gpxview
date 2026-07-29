using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var outputDirectory = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src", "GpxView.App", "Assets"));
Directory.CreateDirectory(outputDirectory);

using var master = DrawMaster(1024);
using var preview = Resize(master, 256);
preview.Save(Path.Combine(outputDirectory, "GpxView.png"), ImageFormat.Png);

var storeAssetsDirectory = Path.Combine(outputDirectory, "Store");
Directory.CreateDirectory(storeAssetsDirectory);
WritePng(master, 44, Path.Combine(storeAssetsDirectory, "Square44x44Logo.png"));
WritePng(master, 150, Path.Combine(storeAssetsDirectory, "Square150x150Logo.png"));
WritePng(master, 50, Path.Combine(storeAssetsDirectory, "StoreLogo.png"));
WritePng(master, 300, Path.Combine(storeAssetsDirectory, "AppTileIcon300.png"));

var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var frames = sizes.Select(size =>
{
    using var bitmap = Resize(master, size);
    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Png);
    return (Size: size, Data: stream.ToArray());
}).ToArray();
WriteIco(Path.Combine(outputDirectory, "GpxView.ico"), frames);
Console.WriteLine($"Generated app and Store assets in {outputDirectory}");

static void WritePng(Image source, int size, string path)
{
    using var bitmap = Resize(source, size);
    bitmap.Save(path, ImageFormat.Png);
}

static Bitmap DrawMaster(int size)
{
    var scale = size / 256f;
    float V(float value) => value * scale;

    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    bitmap.SetResolution(96, 96);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    graphics.CompositingQuality = CompositingQuality.HighQuality;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    graphics.Clear(Color.Transparent);

    var bounds = new RectangleF(V(16), V(14), V(224), V(224));
    using var shape = RoundedRectangle(bounds, V(56));

    // Layered soft shadow; subtle enough for both light and dark taskbars.
    for (var spread = 8; spread >= 1; spread--)
    {
        var alpha = 3 + (8 - spread);
        var shadowBounds = RectangleF.Inflate(bounds, V(spread * .7f), V(spread * .7f));
        shadowBounds.Y += V(4 + spread * .45f);
        using var shadowPath = RoundedRectangle(shadowBounds, V(56 + spread * .6f));
        using var shadowBrush = new SolidBrush(Color.FromArgb(alpha, 2, 18, 45));
        graphics.FillPath(shadowBrush, shadowPath);
    }

    using (var gradient = new LinearGradientBrush(bounds, Color.FromArgb(7, 66, 177), Color.FromArgb(21, 154, 235), 42f))
        graphics.FillPath(gradient, shape);

    graphics.SetClip(shape);
    using (var glowPath = new GraphicsPath())
    {
        glowPath.AddEllipse(V(115), V(-75), V(210), V(210));
        using var glow = new PathGradientBrush(glowPath)
        {
            CenterColor = Color.FromArgb(58, 170, 229, 255),
            SurroundColors = new[] { Color.FromArgb(0, 170, 229, 255) }
        };
        graphics.FillPath(glow, glowPath);
    }

    using var contourPen = new Pen(Color.FromArgb(37, 231, 248, 255), V(3))
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round
    };
    using (var contours = new GraphicsPath())
    {
        contours.StartFigure();
        contours.AddBezier(V(29), V(85), V(75), V(57), V(108), V(98), V(153), V(71));
        contours.AddBezier(V(153), V(71), V(187), V(50), V(215), V(68), V(238), V(52));
        contours.StartFigure();
        contours.AddBezier(V(23), V(195), V(65), V(166), V(108), V(207), V(150), V(177));
        contours.AddBezier(V(150), V(177), V(184), V(153), V(216), V(178), V(243), V(155));
        contours.StartFigure();
        contours.AddBezier(V(30), V(134), V(63), V(116), V(89), V(140), V(114), V(124));
        graphics.DrawPath(contourPen, contours);
    }

    using var route = new GraphicsPath();
    route.StartFigure();
    route.AddBezier(V(57), V(176), V(80), V(176), V(84), V(153), V(105), V(155));
    route.AddBezier(V(105), V(155), V(127), V(157), V(126), V(123), V(150), V(120));
    route.AddBezier(V(150), V(120), V(172), V(118), V(175), V(141), V(194), V(128));
    route.AddBezier(V(194), V(128), V(211), V(116), V(202), V(86), V(205), V(72));

    using var routeShadow = new Pen(Color.FromArgb(92, 1, 35, 91), V(18))
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round
    };
    using var routePen = new Pen(Color.FromArgb(250, 253, 255), V(15))
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round
    };
    using (var transform = new Matrix())
    {
        transform.Translate(V(0), V(3));
        using var shadowRoute = (GraphicsPath)route.Clone();
        shadowRoute.Transform(transform);
        graphics.DrawPath(routeShadow, shadowRoute);
    }
    graphics.DrawPath(routePen, route);

    using var markerOuter = new SolidBrush(Color.FromArgb(248, 253, 255));
    using var markerStart = new SolidBrush(Color.FromArgb(21, 154, 235));
    using var markerEnd = new SolidBrush(Color.FromArgb(8, 84, 191));
    graphics.FillEllipse(markerOuter, V(45), V(164), V(24), V(24));
    graphics.FillEllipse(markerStart, V(51), V(170), V(12), V(12));
    graphics.FillEllipse(markerOuter, V(191), V(58), V(28), V(28));
    graphics.FillEllipse(markerEnd, V(198), V(65), V(14), V(14));

    graphics.ResetClip();
    using var highlight = new Pen(Color.FromArgb(45, 255, 255, 255), V(2));
    using var innerShape = RoundedRectangle(new RectangleF(V(18), V(16), V(220), V(220)), V(54));
    graphics.DrawPath(highlight, innerShape);
    return bitmap;
}

static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
{
    var diameter = radius * 2;
    var path = new GraphicsPath();
    path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
    path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
    path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
    path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
    path.CloseFigure();
    return path;
}

static Bitmap Resize(Image source, int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.CompositingMode = CompositingMode.SourceCopy;
    graphics.CompositingQuality = CompositingQuality.HighQuality;
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    graphics.SmoothingMode = SmoothingMode.HighQuality;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    graphics.DrawImage(source, new Rectangle(0, 0, size, size));
    return bitmap;
}

static void WriteIco(string path, IReadOnlyList<(int Size, byte[] Data)> frames)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)frames.Count);
    var offset = 6 + 16 * frames.Count;
    foreach (var frame in frames)
    {
        writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
        writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(frame.Data.Length);
        writer.Write(offset);
        offset += frame.Data.Length;
    }
    foreach (var frame in frames) writer.Write(frame.Data);
}
