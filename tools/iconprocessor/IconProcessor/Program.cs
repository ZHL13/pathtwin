using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

const int OutputSize = 1000;
const int CornerRadius = 120;
const int BackgroundThreshold = 14;

var repoRoot = FindRepoRoot();
var sourcePath = Path.Combine(repoRoot, "assets", "icon-source.png");
var outputPng = Path.Combine(repoRoot, "icon.png");
var outputIco = Path.Combine(repoRoot, "src", "PathTwin.App", "icon.ico");

if (!File.Exists(sourcePath))
{
    sourcePath = outputPng;
}

using var source = new Bitmap(sourcePath);
var background = AverageCornerColor(source, sampleSize: 16);
var contentBounds = FindContentBounds(source, background, BackgroundThreshold);
var squareCrop = ExpandToSquare(contentBounds, source.Width, source.Height);

using var result = RenderRoundedIcon(source, squareCrop);
SavePng(result, outputPng);
SaveIco(result, outputIco);

Console.WriteLine($"Source: {sourcePath}");
Console.WriteLine($"Detected bounds: {contentBounds}");
Console.WriteLine($"Square crop: {squareCrop}");
Console.WriteLine($"Saved: {outputPng}");
Console.WriteLine($"Saved: {outputIco}");

static string FindRepoRoot()
{
    var current = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "src", "PathTwin.App", "PathTwin.App.csproj")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new DirectoryNotFoundException("Could not find repository root.");
}

static Color AverageCornerColor(Bitmap source, int sampleSize)
{
    long r = 0;
    long g = 0;
    long b = 0;
    var count = 0;

    foreach (var (startX, startY) in new[]
    {
        (0, 0),
        (source.Width - sampleSize, 0),
        (0, source.Height - sampleSize),
        (source.Width - sampleSize, source.Height - sampleSize)
    })
    {
        for (var y = startY; y < startY + sampleSize; y++)
        {
            for (var x = startX; x < startX + sampleSize; x++)
            {
                var pixel = source.GetPixel(x, y);
                r += pixel.R;
                g += pixel.G;
                b += pixel.B;
                count++;
            }
        }
    }

    return Color.FromArgb((int)(r / count), (int)(g / count), (int)(b / count));
}

static Rectangle FindContentBounds(Bitmap source, Color background, int threshold)
{
    var minX = source.Width;
    var minY = source.Height;
    var maxX = -1;
    var maxY = -1;

    for (var y = 0; y < source.Height; y++)
    {
        for (var x = 0; x < source.Width; x++)
        {
            var pixel = source.GetPixel(x, y);
            var distance = Math.Abs(pixel.R - background.R)
                + Math.Abs(pixel.G - background.G)
                + Math.Abs(pixel.B - background.B);

            if (distance <= threshold)
            {
                continue;
            }

            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
    }

    if (maxX < minX || maxY < minY)
    {
        throw new InvalidOperationException("Could not detect icon content bounds.");
    }

    return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
}

static Rectangle ExpandToSquare(Rectangle bounds, int maxWidth, int maxHeight)
{
    var side = Math.Max(bounds.Width, bounds.Height);
    var centerX = bounds.Left + bounds.Width / 2;
    var centerY = bounds.Top + bounds.Height / 2;
    var x = centerX - side / 2;
    var y = centerY - side / 2;

    x = Math.Clamp(x, 0, maxWidth - side);
    y = Math.Clamp(y, 0, maxHeight - side);

    return new Rectangle(x, y, side, side);
}

static Bitmap RenderRoundedIcon(Bitmap source, Rectangle crop)
{
    var result = new Bitmap(OutputSize, OutputSize, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(result);
    graphics.Clear(Color.Transparent);
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    graphics.CompositingQuality = CompositingQuality.HighQuality;

    using var path = RoundedRectangle(new Rectangle(0, 0, OutputSize, OutputSize), CornerRadius);
    graphics.SetClip(path);
    graphics.DrawImage(
        source,
        new Rectangle(0, 0, OutputSize, OutputSize),
        crop,
        GraphicsUnit.Pixel);

    return result;
}

static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
{
    var path = new GraphicsPath();
    var diameter = radius * 2;
    path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
    path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
    path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
    path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
    path.CloseFigure();
    return path;
}

static void SavePng(Bitmap image, string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
    var tempPath = outputPath + ".tmp";
    image.Save(tempPath, ImageFormat.Png);
    File.Copy(tempPath, outputPath, overwrite: true);
    File.Delete(tempPath);
}

static void SaveIco(Bitmap source, string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
    var sizes = new[] { 256, 128, 64, 48, 32, 16 };
    var pngs = sizes.Select(size => RenderPngBytes(source, size)).ToArray();

    using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
    using var writer = new BinaryWriter(stream);
    writer.Write((short)0);
    writer.Write((short)1);
    writer.Write((short)sizes.Length);

    var dataOffset = 6 + sizes.Length * 16;
    for (var i = 0; i < sizes.Length; i++)
    {
        var size = sizes[i];
        writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(pngs[i].Length);
        writer.Write(dataOffset);
        dataOffset += pngs[i].Length;
    }

    foreach (var png in pngs)
    {
        writer.Write(png);
    }
}

static byte[] RenderPngBytes(Bitmap source, int size)
{
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(bitmap))
    {
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, size, size);
    }

    using var memory = new MemoryStream();
    bitmap.Save(memory, ImageFormat.Png);
    return memory.ToArray();
}
