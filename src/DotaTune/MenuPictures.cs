using System.Security.Cryptography;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Menu;
using SkiaSharp;

namespace DotaTune;

/// <summary>
/// The pictures the "Menu background" page offers: samples built into the program as JPEG files
/// cut to 16:9, and pictures the person added, kept as PNG files in the shape of the screen. A
/// choice is kept by id: "black", "sample:street" or "picture:&lt;file&gt;"; null is the game's own
/// background.
/// </summary>
internal sealed class MenuPictures
{
    public const string BlackId = "black";

    /// <summary>The largest picture kept: the size checked in the menu on build 6967.</summary>
    public const int MaxWidth = 1920;

    /// <summary>The largest image read, so a huge panorama does not take gigabytes to decode: 100 megapixels, about 400 MB.</summary>
    public const long MaxSourcePixels = 100_000_000;

    private const string SamplePrefix = "sample:";
    private const string PicturePrefix = "picture:";

    /// <summary>Samples in the order the page shows them; each is the embedded MenuBackgrounds.&lt;name&gt;.jpg.</summary>
    public static readonly IReadOnlyList<string> Samples = ["street", "samurai", "glasses", "glance", "forest", "smile", "hills"];

    private readonly string _folder;
    private readonly FileLog _log;

    public MenuPictures(string folder, FileLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(log);
        _folder = folder;
        _log = log;
    }

    public static string SampleId(string name) => SamplePrefix + name;

    public static bool IsAdded(string? id) => id is not null && id.StartsWith(PicturePrefix, StringComparison.Ordinal);

    public static MenuBackgroundKind KindOf(string? id) => id switch
    {
        null => MenuBackgroundKind.Game,
        BlackId => MenuBackgroundKind.Black,
        _ => MenuBackgroundKind.Picture,
    };

    /// <summary>Ids of the added pictures, oldest first.</summary>
    public IReadOnlyList<string> Added()
    {
        if (!Directory.Exists(_folder))
        {
            return [];
        }

        return [.. new DirectoryInfo(_folder).GetFiles("*.png")
            .OrderBy(f => f.CreationTimeUtc)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => PicturePrefix + f.Name)];
    }

    /// <summary>The file of a sample (JPEG) or of an added picture (PNG).</summary>
    /// <exception cref="FileNotFoundException">There is no such picture.</exception>
    public Stream Open(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (SampleName(id) is { } name)
        {
            return typeof(MenuPictures).Assembly.GetManifestResourceStream($"MenuBackgrounds.{name}.jpg")
                ?? throw new FileNotFoundException($"Sample {name} is not built into the program");
        }

        return FileOf(id) is { } file ? File.OpenRead(file) : throw new FileNotFoundException($"{id} is not a menu picture");
    }

    /// <summary>The picture as PNG, the form the game reads.</summary>
    /// <exception cref="FileNotFoundException">There is no such picture.</exception>
    /// <exception cref="InvalidDataException">A sample is not an image that can be read.</exception>
    public byte[] Png(string id)
    {
        using Stream stream = Open(id);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return SampleName(id) is null ? copy.ToArray() : ToMenuPng(copy.ToArray());
    }

    /// <summary>Keeps a copy of the image in the shape the menu shows; the same image added twice is kept once.</summary>
    /// <returns>The id of the picture.</returns>
    /// <exception cref="InvalidDataException">The file is not an image that can be read.</exception>
    public string Add(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] png = ToMenuPng(File.ReadAllBytes(path));
        string name = Convert.ToHexStringLower(SHA1.HashData(png))[..16] + ".png";
        string target = Path.Combine(_folder, name);
        if (!File.Exists(target))
        {
            Directory.CreateDirectory(_folder);
            string temp = target + ".tmp";
            File.WriteAllBytes(temp, png);
            File.Move(temp, target, overwrite: true);
        }

        _log.Info($"Menu picture {name} added from {Path.GetFileName(path)}, {png.Length / 1024} KB");
        return PicturePrefix + name;
    }

    /// <summary>Deletes DotaTune's copy of an added picture; the file it was made from is not touched.</summary>
    public void Remove(string id)
    {
        if (FileOf(id) is { } file && File.Exists(file))
        {
            File.Delete(file);
            _log.Info($"Menu picture {Path.GetFileName(file)} removed");
        }
    }

    /// <summary>
    /// The widest 16:9 part around the centre of the image, scaled down to at most 1920x1080 and
    /// laid on black, as PNG. The menu stretches its picture over the whole screen, so another shape
    /// would come out squeezed.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not an image that can be read, or it is over <see cref="MaxSourcePixels"/>.</exception>
    internal static byte[] ToMenuPng(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // The image is only decoded when drawn, so its size is known before the memory is taken.
        using SKImage image = SKImage.FromEncodedData(source) ?? throw new InvalidDataException("The file is not a picture");
        if ((long)image.Width * image.Height > MaxSourcePixels)
        {
            throw new InvalidDataException($"The picture is {image.Width}x{image.Height}, over {MaxSourcePixels / 1_000_000} megapixels");
        }

        float width = image.Width;
        float height = image.Height;
        SKRect cut = width * 9 > height * 16
            ? SKRect.Create((width - (height * 16 / 9)) / 2, 0, height * 16 / 9, height)
            : SKRect.Create(0, (height - (width * 9 / 16)) / 2, width, width * 9 / 16);
        int outWidth = Math.Max(1, (int)Math.Min(MaxWidth, Math.Round(cut.Width)));
        int outHeight = Math.Max(1, (int)Math.Round(outWidth * 9.0 / 16));
        using SKSurface surface = SKSurface.Create(new SKImageInfo(outWidth, outHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidDataException($"A picture of {outWidth}x{outHeight} cannot be made");
        surface.Canvas.Clear(SKColors.Black);
        surface.Canvas.DrawImage(image, cut, SKRect.Create(outWidth, outHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using SKImage result = surface.Snapshot();
        using SKData data = result.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string? SampleName(string id) =>
        id.StartsWith(SamplePrefix, StringComparison.Ordinal) && Samples.Contains(id[SamplePrefix.Length..]) ? id[SamplePrefix.Length..] : null;

    /// <summary>The file of an added picture; null for any other id, and for a name that would leave the folder.</summary>
    private string? FileOf(string id)
    {
        if (!IsAdded(id))
        {
            return null;
        }

        string name = id[PicturePrefix.Length..];
        return name.Length > 0 && name == Path.GetFileName(name) && name.EndsWith(".png", StringComparison.Ordinal)
            ? Path.Combine(_folder, name)
            : null;
    }
}
