using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace TiaOpenness.Gui.Tests.Tooling;

// The release command runs this file with the installed Windows Desktop SDK.
// Keep the input vocabulary to filled SVG paths and object-bounding-box gradients.
internal static class AppIconGenerator
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [STAThread]
    public static int Main(string[] args)
    {
        bool check = args.Contains("-Check");
        try { Generate(args[0], check); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or System.Xml.XmlException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        Console.WriteLine($"generate-app-icon: {(check ? "verified" : "generated")} nine ICO frames, the README image and the Workbench drawings.");
        return 0;
    }

    internal static void Generate(string root, bool check)
    {
        string assets = Path.Combine(root, "src/Studio/Gui/Assets");
        var small = XElement.Load(Path.Combine(assets, "AppIconSmall.svg"));
        var large = XElement.Load(Path.Combine(assets, "AppIcon.svg"));
        var largeDrawing = ReadDrawing(large);
        var frames = Sizes.Select(size => Render(size <= 32 ? ReadDrawing(small, size) : largeDrawing, size)).ToArray();
        var readme = Render(largeDrawing, 96);
        string xaml = ResourceDictionary(small, large);
        string icoPath = Path.Combine(root, "build-tools/assets/AppIcon.ico");
        string titlePath = Path.Combine(root, "src/Studio/Gui/Themes/Palette.AppIcon.xaml");
        string pngPath = Path.Combine(root, "docs/assets/app-icon.png");
        if (check)
        {
            using var ico = File.OpenRead(icoPath);
            var stored = new IconBitmapDecoder(ico, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (stored.Frames.Count != frames.Length) throw new InvalidDataException("Stale ICO frame count.");
            for (int i = 0; i < frames.Length; i++) CheckPixels(frames[i], stored.Frames[i], icoPath);
            using var png = File.OpenRead(pngPath);
            CheckPixels(readme, BitmapFrame.Create(png), pngPath);
            if (png.Length > 40 * 1024) throw new InvalidDataException("README icon exceeds 40 KB.");
            if (File.ReadAllText(titlePath).ReplaceLineEndings("\n") != xaml) throw new InvalidDataException("Stale Workbench drawing.");
            return;
        }
        byte[] readmePng = Encode(readme);
        if (readmePng.Length > 40 * 1024) throw new InvalidDataException("README icon exceeds 40 KB.");
        File.WriteAllBytes(icoPath, Icon(frames));
        File.WriteAllBytes(pngPath, readmePng);
        File.WriteAllText(titlePath, xaml);
    }

    private sealed record Vector(DrawingGroup Drawing, double Width);

    private static bool VisibleAt(XElement path, int size) => size >= ((int?)path.Attribute("data-min-size") ?? 0);

    private static Vector ReadDrawing(XElement svg, int size = 256)
    {
        var group = new DrawingGroup();
        if ((string?)svg.Attribute("data-guides-x") is { } guides)
            group.GuidelineSet = new GuidelineSet { GuidelinesX = DoubleCollection.Parse(guides) };
        var gradients = svg.Element(Svg + "defs")?.Elements().ToDictionary(e => (string)e.Attribute("id")!, Gradient);
        using (var dc = group.Open())
        {
            foreach (var path in svg.Elements().Where(e => e.Name != Svg + "defs"))
            {
                if (path.Name != Svg + "path") throw new InvalidDataException("Unsupported icon element " + path.Name);
                if (!VisibleAt(path, size)) continue;
                string fill = (string)path.Attribute("fill")!;
                Brush brush = fill.StartsWith("url(#", StringComparison.Ordinal)
                    ? gradients![fill[5..^1]] : new SolidColorBrush(ColorOf(fill));
                dc.PushOpacity((double?)path.Attribute("fill-opacity") ?? 1);
                dc.DrawGeometry(brush, null, Geometry.Parse((string)path.Attribute("d")!));
                dc.Pop();
            }
        }
        return new Vector(group, (double)svg.Attribute("width")!);
    }

    private static Color ColorOf(string value) => (Color)ColorConverter.ConvertFromString(value);

    private static GradientBrush Gradient(XElement element)
    {
        double At(string name, double fallback) => (double?)element.Attribute(name) ?? fallback;
        var center = new Point(At("cx", .5), At("cy", .5));
        GradientBrush brush = element.Name.LocalName switch
        {
            "linearGradient" => new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(At("x2", 1), At("y2", 0)) },
            "radialGradient" => new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = At("r", .5), RadiusY = At("r", .5) },
            _ => throw new InvalidDataException("Unsupported icon gradient " + element.Name)
        };
        foreach (var stop in element.Elements(Svg + "stop"))
        {
            var color = ColorOf((string)stop.Attribute("stop-color")!);
            color.A = (byte)Math.Round(255 * ((double?)stop.Attribute("stop-opacity") ?? 1));
            brush.GradientStops.Add(new GradientStop(color, (double)stop.Attribute("offset")!));
        }
        return brush;
    }

    private static RenderTargetBitmap Render(Vector vector, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double scale = size / vector.Width;
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawDrawing(vector.Drawing);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] Icon(BitmapSource[] frames)
    {
        var images = frames.Select(Encode).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Length);
        int offset = 6 + 16 * frames.Length;
        for (int i = 0; i < frames.Length; i++)
        {
            byte dimension = (byte)(frames[i].PixelWidth % 256);
            writer.Write(dimension);
            writer.Write(dimension);
            writer.Write((ushort)0); // palette size and reserved byte
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images) writer.Write(image);
        return stream.ToArray();
    }

    private static void CheckPixels(BitmapSource expected, BitmapSource actual, string path)
    {
        int size = expected.PixelWidth;
        if (actual.PixelWidth != size || actual.PixelHeight != size) throw new InvalidDataException($"Expected {size}px in {path}");
        byte[] Pixels(BitmapSource source)
        {
            var pixels = new byte[size * size * 4];
            new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0).CopyPixels(pixels, size * 4, 0);
            return pixels;
        }
        var rendered = Pixels(expected);
        var stored = Pixels(actual);
        for (int i = 0; i < rendered.Length; i++)
            if (Math.Abs(stored[i] - rendered[i]) > 2) throw new InvalidDataException($"Stale {size}px image in {path} at pixel {i / 4}.");
    }

    private static string ResourceDictionary(XElement small, XElement large)
    {
        XElement Drawing(bool highContrast)
        {
            XElement Layer(XElement path)
            {
                string role = (string)path.Attribute("class")!;
                string brush = highContrast ? role switch
                {
                    "tile" => "{DynamicResource Primer.brandGradient}",
                    "plate" => "{DynamicResource Primer.onPrimary}",
                    _ => throw new InvalidDataException("Unknown icon colour role " + role)
                } : (string)path.Attribute("fill")!;
                return new XElement(Wpf + "GeometryDrawing", new XAttribute("Brush", brush), new XAttribute("Geometry", (string)path.Attribute("d")!));
            }
            var guides = new XElement(Wpf + "DrawingGroup.GuidelineSet",
                new XElement(Wpf + "GuidelineSet", new XAttribute("GuidelinesX", (string)small.Attribute("data-guides-x")!)));
            var layers = small.Elements(Svg + "path").Where(path => VisibleAt(path, 20)).Select(Layer);
            return new XElement(Wpf + "DrawingImage", new XAttribute(Xaml + "Key", highContrast ? "AppIcon.HighContrast" : "AppIcon.Small"),
                new XElement(Wpf + "DrawingImage.Drawing", new XElement(Wpf + "DrawingGroup", guides, layers)));
        }
        // DrawingImage sizes to its ink. Include the SVG viewport so the large tile retains its transparent margin.
        var gradients = large.Element(Svg + "defs")!.Elements().ToDictionary(e => (string)e.Attribute("id")!, Gradient);
        XElement LargeLayer(XElement path)
        {
            string fill = (string)path.Attribute("fill")!;
            var layer = new XElement(Wpf + "GeometryDrawing", new XAttribute("Geometry", (string)path.Attribute("d")!));
            if (fill.StartsWith("url(#", StringComparison.Ordinal))
            {
                var gradient = gradients[fill[5..^1]];
                var brush = gradient switch
                {
                    LinearGradientBrush linear => new XElement(Wpf + "LinearGradientBrush",
                        new XAttribute("StartPoint", linear.StartPoint.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("EndPoint", linear.EndPoint.ToString(CultureInfo.InvariantCulture))),
                    RadialGradientBrush radial => new XElement(Wpf + "RadialGradientBrush",
                        new XAttribute("Center", radial.Center.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("GradientOrigin", radial.GradientOrigin.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("RadiusX", radial.RadiusX), new XAttribute("RadiusY", radial.RadiusY)),
                    _ => throw new InvalidDataException("Unsupported icon brush.")
                };
                foreach (var stop in gradient.GradientStops)
                    brush.Add(new XElement(Wpf + "GradientStop", new XAttribute("Offset", stop.Offset), new XAttribute("Color", stop.Color.ToString())));
                layer.Add(new XElement(Wpf + "GeometryDrawing.Brush", brush));
            }
            else layer.SetAttributeValue("Brush", fill);
            return path.Attribute("fill-opacity") is { } opacity
                ? new XElement(Wpf + "DrawingGroup", new XAttribute("Opacity", opacity.Value), layer) : layer;
        }
        var viewport = new XElement(Wpf + "GeometryDrawing", new XAttribute("Brush", "Transparent"),
            new XElement(Wpf + "GeometryDrawing.Geometry", new XElement(Wpf + "RectangleGeometry",
                new XAttribute("Rect", $"0,0,{large.Attribute("width")!.Value},{large.Attribute("height")!.Value}"))));
        var largeImage = new XElement(Wpf + "DrawingImage", new XAttribute(Xaml + "Key", "AppIcon.Large"),
            new XElement(Wpf + "DrawingImage.Drawing",
                new XElement(Wpf + "DrawingGroup", viewport, large.Elements(Svg + "path").Select(LargeLayer))));
        var dict = new XElement(Wpf + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", Xaml),
            new XComment(" Generated from AppIconSmall.svg and AppIcon.svg by generate-app-icon. "), Drawing(false), Drawing(true), largeImage);
        return dict.ToString().ReplaceLineEndings("\n") + "\n";
    }
}
