using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using DotaTune.Core.Game;
using ValvePak;

namespace DotaTune.Core.Menu;

/// <summary>What the main menu shows behind its panels.</summary>
public enum MenuBackgroundKind
{
    /// <summary>Whatever the game picks: event scenes, hero models, particles.</summary>
    Game,

    /// <summary>Valve's empty layout in place of every background: a black menu and the least work for the card.</summary>
    Black,

    /// <summary>A picture in place of every background, shown by Valve's default background layout.</summary>
    Picture,
}

/// <summary>
/// The menu background files of the installed game. The dashboard loads one of its background
/// layouts by name (an event, a treasure, the default one); the pack puts another layout of the
/// same game under every one of those names, so nothing is compiled and the files always come from
/// the current build. Checked on build 6967: Valve's empty layout leaves a black menu with every
/// panel working, and the card does 65 W instead of 106 W at 120 FPS.
/// </summary>
/// <param name="Layouts">Every background layout the dashboard can load, by its compiled name.</param>
/// <param name="EmptyLayout">Valve's empty layout: one panel and the common styles.</param>
/// <param name="DefaultLayout">Valve's default background: a picture under a 3D scene; null when the game has none.</param>
/// <param name="DefaultStyle">The compiled stylesheet of the default background; null when the game has none.</param>
/// <param name="PictureTemplate">A panorama texture stored as a PNG, the model for a picture; null when the game has none.</param>
public sealed partial record GameMenuBackground(IReadOnlyList<string> Layouts, byte[] EmptyLayout, byte[]? DefaultLayout, byte[]? DefaultStyle, byte[]? PictureTemplate)
{
    public const string EmptyLayoutPath = "panorama/layout/empty.vxml_c";
    public const string DefaultLayoutPath = "panorama/layout/dashboard_background_default.vxml_c";
    public const string DefaultStylePath = "panorama/styles/dashboard_background_default.vcss_c";

    /// <summary>
    /// The images of the default background, in the menu and in the menu during a match, and the
    /// names of the same length the picture is shown under. The style of the screen before loading
    /// names the menu image too, at 240x135, and on build 6967 a picture under the image's own name
    /// came out at that size, its top left corner over the whole screen, whatever this stylesheet
    /// said; under a name no other style knows it fills the screen.
    /// </summary>
    public static readonly IReadOnlyList<(string Image, string Picture)> PictureImages =
    [
        ("panorama/images/backgrounds/dashboard_background_psd.vtex", "panorama/images/backgrounds/dashboard_background_dtt.vtex"),
        ("panorama/images/backgrounds/dashboard_background_ingame_psd.vtex", "panorama/images/backgrounds/dashboard_background_ingame_dtt.vtex"),
    ];

    private const string TemplateFolder = "panorama/images/backgrounds/";

    /// <summary>True when the game can show a picture: the default layout and style are as expected and there is a PNG texture to model on.</summary>
    public bool CanShowPicture =>
        DefaultLayout is not null && PictureTemplate is not null && DefaultStyle is not null
        && PanoramaStyle.WithPicture(DefaultStyle, PictureImages, 1, 1) is not null;

    /// <summary>Reads game\dota\pak01_dir.vpk. Only reads.</summary>
    /// <exception cref="FileNotFoundException">The game has no pak01_dir.vpk.</exception>
    /// <exception cref="InvalidDataException">The archive cannot be read, or has no empty layout or no background layouts: the game changed.</exception>
    public static GameMenuBackground Read(string dotaFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dotaFolder);
        string pak = Path.Combine(dotaFolder, "pak01_dir.vpk");
        if (!File.Exists(pak))
        {
            throw new FileNotFoundException("The game has no pak01_dir.vpk", pak);
        }

        try
        {
            using var package = new Package();
            package.Read(pak);
            PackageEntry empty = package.FindEntry(EmptyLayoutPath) ?? throw new InvalidDataException($"{EmptyLayoutPath} is not in {pak}");
            List<string> layouts = [.. Entries(package, "vxml_c").Select(e => e.GetFullPath()).Where(IsBackgroundLayout).Order(StringComparer.Ordinal)];
            if (layouts.Count == 0)
            {
                throw new InvalidDataException($"{pak} has no menu background layouts");
            }

            byte[]? defaultLayout = package.FindEntry(DefaultLayoutPath) is { } layout ? ReadEntry(package, layout) : null;
            byte[]? defaultStyle = package.FindEntry(DefaultStylePath) is { } style ? ReadEntry(package, style) : null;
            byte[]? template = Entries(package, "vtex_c")
                .Where(e => e.GetFullPath().StartsWith(TemplateFolder, StringComparison.Ordinal))
                .OrderBy(e => e.GetFullPath(), StringComparer.Ordinal)
                .Select(e => ReadEntry(package, e))
                .FirstOrDefault(PanoramaTexture.IsPngTexture);
            return new GameMenuBackground(layouts, ReadEntry(package, empty), defaultLayout, defaultStyle, template);
        }
        catch (Exception ex) when (GameArchive.IsDamage(ex))
        {
            throw new InvalidDataException($"{pak} is not readable: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// A layout the background manager of the dashboard loads by name: dashboard_background_* (not the
    /// manager itself) and the event backgrounds under events/. In build 6967 nothing else in the
    /// game refers to them but their own styles and scripts.
    /// </summary>
    public static bool IsBackgroundLayout(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return DashboardBackground().IsMatch(path) || EventBackground().IsMatch(path);
    }

    /// <summary>
    /// Pack files for the choice: nothing for the game's own background; the empty layout under every
    /// background for black; for a picture, the default layout under every background, the picture
    /// under both of its own names and the default stylesheet that shows it at its size and without
    /// the 3D scene. The game's own images stay as they are.
    /// </summary>
    /// <param name="picture">A PNG file, needed for <see cref="MenuBackgroundKind.Picture"/>.</param>
    /// <exception cref="InvalidOperationException">A picture is asked for and the game cannot show one.</exception>
    /// <exception cref="InvalidDataException">The picture is not a PNG file.</exception>
    public Dictionary<string, byte[]> PackFiles(MenuBackgroundKind kind, byte[]? picture = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        switch (kind)
        {
            case MenuBackgroundKind.Black:
                foreach (string layout in Layouts)
                {
                    files[layout] = EmptyLayout;
                }

                break;
            case MenuBackgroundKind.Picture:
                ArgumentNullException.ThrowIfNull(picture);
                (int width, int height) = PanoramaTexture.PngSize(picture) ?? throw new InvalidDataException("The picture is not a PNG file");
                if (DefaultLayout is null || PictureTemplate is null || DefaultStyle is null
                    || PanoramaStyle.WithPicture(DefaultStyle, PictureImages, width, height) is not { } style)
                {
                    throw new InvalidOperationException("The game's default background is not as expected, a picture cannot be shown");
                }

                foreach (string layout in Layouts.Where(l => l != DefaultLayoutPath))
                {
                    files[layout] = DefaultLayout;
                }

                byte[] texture = PanoramaTexture.WithPicture(PictureTemplate, picture);
                foreach ((string _, string name) in PictureImages)
                {
                    files[name + "_c"] = texture;
                }

                files[DefaultStylePath] = style;
                break;
            default:
                break;
        }

        return files;
    }

    private static List<PackageEntry> Entries(Package package, string extension) =>
        package.Entries?.GetValueOrDefault(extension) ?? [];

    private static byte[] ReadEntry(Package package, PackageEntry entry)
    {
        package.ReadEntry(entry, out byte[] data);
        return data;
    }

    [GeneratedRegex(@"^panorama/layout/dashboard_background_(?!manager\.)[a-z0-9_]+\.vxml_c$", RegexOptions.CultureInvariant)]
    private static partial Regex DashboardBackground();

    [GeneratedRegex(@"^panorama/layout/events/(?:[a-z0-9_]+/)*[a-z0-9_]*background[a-z0-9_]*\.vxml_c$", RegexOptions.CultureInvariant)]
    private static partial Regex EventBackground();
}

/// <summary>
/// A compiled panorama stylesheet. Its DATA block is a CRC of the source, the count of images the
/// styles name, each image as its name, width, height and the CRC of its source file, then the
/// styles as plain text. Panorama maps a texture by the size kept for its name, so a picture of
/// another size shows only a corner of itself (seen on build 6967: 1920x1080 under a size of
/// 240x135 showed its top left eighth).
/// </summary>
public static class PanoramaStyle
{
    private const int BlockEntrySize = 12;

    /// <summary>
    /// A copy of the default background's stylesheet that names the picture in place of each image,
    /// in the image table and in the styles, keeps it at the picture's size and gives the 3D scene
    /// over the picture no area; null unless every image, its use in the styles and the scene rule
    /// are found. Every change keeps the length, so no offset in the file moves.
    /// </summary>
    /// <param name="images">Each image and the picture's name for it, of the same length.</param>
    public static byte[]? WithPicture(byte[] style, IReadOnlyList<(string Image, string Picture)> images, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(images);
        if (images.Any(i => i.Image.Length != i.Picture.Length))
        {
            throw new ArgumentException("A picture's name must be as long as the image it replaces", nameof(images));
        }

        if (DataBlock(style) is not (int start, int length) || width is < 1 or > ushort.MaxValue || height is < 1 or > ushort.MaxValue)
        {
            return null;
        }

        byte[] copy = (byte[])style.Clone();
        Span<byte> data = copy.AsSpan(start, length);
        if (data.Length < 6)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        int at = 6;
        var found = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            int end = data[at..].IndexOf((byte)0);
            if (end < 0 || at + end + 9 > data.Length)
            {
                return null;
            }

            string name = Encoding.ASCII.GetString(data.Slice(at, end));
            if (images.FirstOrDefault(m => m.Image == name).Picture is { } picture)
            {
                Encoding.ASCII.GetBytes(picture).CopyTo(data[at..]);
                BinaryPrimitives.WriteUInt16LittleEndian(data[(at + end + 1)..], (ushort)width);
                BinaryPrimitives.WriteUInt16LittleEndian(data[(at + end + 3)..], (ushort)height);
                found.Add(name);
            }

            at += end + 9;
        }

        // By name: an image listed twice is renamed twice and still counts once.
        Span<byte> css = data[at..];
        if (found.Count != images.Count)
        {
            return null;
        }

        foreach ((string image, string picture) in images)
        {
            if (!Rename(css, image, picture))
            {
                return null;
            }
        }

        return HideScene(css) ? copy : null;
    }

    /// <summary>Puts the new name over every use of the old one in the styles; false when there is none.</summary>
    private static bool Rename(Span<byte> css, string from, string to)
    {
        byte[] old = Encoding.ASCII.GetBytes(from);
        byte[] replacement = Encoding.ASCII.GetBytes(to);
        bool any = false;
        for (int start = 0, hit; (hit = css[start..].IndexOf(old)) >= 0; start += hit + old.Length)
        {
            replacement.CopyTo(css[(start + hit)..]);
            any = true;
        }

        return any;
    }

    /// <summary>Sets width and height of the first #BackgroundMap rule to 0px in place, padding with spaces.</summary>
    private static bool HideScene(Span<byte> css)
    {
        int rule = css.IndexOf("#BackgroundMap{"u8);
        if (rule < 0)
        {
            return false;
        }

        Span<byte> body = css[(rule + "#BackgroundMap{"u8.Length)..];
        int close = body.IndexOf((byte)'}');
        if (close < 0)
        {
            return false;
        }

        body = body[..close];
        return ZeroSize(body, "width:"u8) && ZeroSize(body, "height:"u8);
    }

    private static bool ZeroSize(Span<byte> body, ReadOnlySpan<byte> property)
    {
        // Only a property of its own: "max-width:" must not match.
        int at = -1;
        for (int from = 0; from < body.Length;)
        {
            int hit = body[from..].IndexOf(property);
            if (hit < 0)
            {
                break;
            }

            hit += from;
            if (hit == 0 || body[hit - 1] is (byte)';' or (byte)' ')
            {
                at = hit;
                break;
            }

            from = hit + 1;
        }

        if (at < 0)
        {
            return false;
        }

        Span<byte> value = body[(at + property.Length)..];
        int end = value.IndexOf((byte)';');
        if (end < 4)
        {
            return false;
        }

        value = value[..end];
        value.Fill((byte)' ');
        " 0px"u8.CopyTo(value);
        return true;
    }

    private static (int Start, int Length)? DataBlock(ReadOnlySpan<byte> resource)
    {
        if (resource.Length < 16)
        {
            return null;
        }

        long table = 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(resource[8..]);
        long count = BinaryPrimitives.ReadUInt32LittleEndian(resource[12..]);
        if (table + (count * BlockEntrySize) > resource.Length)
        {
            return null;
        }

        for (long i = 0; i < count; i++)
        {
            int entry = (int)(table + (i * BlockEntrySize));
            if (!resource.Slice(entry, 4).SequenceEqual("DATA"u8))
            {
                continue;
            }

            long start = entry + 4 + (long)BinaryPrimitives.ReadUInt32LittleEndian(resource[(entry + 4)..]);
            long length = BinaryPrimitives.ReadUInt32LittleEndian(resource[(entry + 8)..]);
            return start + length > resource.Length ? null : ((int)start, (int)length);
        }

        return null;
    }
}

/// <summary>
/// Panorama textures of format PNG_RGBA8888: the resource header and blocks, then a whole PNG file.
/// A picture is made from such a texture of the game by putting its size into the header and the
/// picture after it, so no compiler is needed and the header always matches the current build.
/// </summary>
public static class PanoramaTexture
{
    public const byte PngRgba8888 = 16;
    private const int BlockEntrySize = 12;
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>True when the texture is one PNG image right after the header, the shape a picture is made in.</summary>
    public static bool IsPngTexture(byte[] texture) => texture is not null && PngAfterHeader(texture) is not null;

    /// <summary>Width and height from the PNG header; null when the bytes are not a PNG file.</summary>
    public static (int Width, int Height)? PngSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || !png[..8].SequenceEqual(PngSignature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(png[16..]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(png[20..]);
        return width is 0 or > ushort.MaxValue || height is 0 or > ushort.MaxValue ? null : ((int)width, (int)height);
    }

    /// <summary>The template's header with the picture's size, then the picture.</summary>
    /// <exception cref="InvalidDataException">The template is not a PNG texture or the picture is not a PNG file.</exception>
    public static byte[] WithPicture(byte[] template, byte[] png)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(png);
        (int headerLength, int data) = PngAfterHeader(template) ?? throw new InvalidDataException("The template is not a panorama texture stored as PNG");
        (int width, int height) = PngSize(png) ?? throw new InvalidDataException("The picture is not a PNG file");
        byte[] result = new byte[headerLength + png.Length];
        template.AsSpan(0, headerLength).CopyTo(result);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(data + 20), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(data + 22), (ushort)height);
        png.CopyTo(result, headerLength);
        return result;
    }

    /// <summary>The header length and the start of the DATA block, or null unless the texture is one PNG image after the header.</summary>
    private static (int HeaderLength, int Data)? PngAfterHeader(ReadOnlySpan<byte> texture)
    {
        if (texture.Length < 16)
        {
            return null;
        }

        // The first field is the length of the header and blocks; the image follows them.
        long headerLength = BinaryPrimitives.ReadUInt32LittleEndian(texture);
        long table = 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(texture[8..]);
        long count = BinaryPrimitives.ReadUInt32LittleEndian(texture[12..]);
        if (headerLength + PngSignature.Length > texture.Length || table + (count * BlockEntrySize) > headerLength)
        {
            return null;
        }

        for (long i = 0; i < count; i++)
        {
            int entry = (int)(table + (i * BlockEntrySize));
            if (!texture.Slice(entry, 4).SequenceEqual("DATA"u8))
            {
                continue;
            }

            long data = entry + 4 + (long)BinaryPrimitives.ReadUInt32LittleEndian(texture[(entry + 4)..]);
            if (data + 28 > headerLength)
            {
                return null;
            }

            // Version, flags, four floats of reflectivity, then width, height, depth, format, mip count.
            int at = (int)data;
            bool single = BinaryPrimitives.ReadUInt16LittleEndian(texture[(at + 24)..]) == 1 && texture[at + 27] == 1;
            bool png = texture[at + 26] == PngRgba8888 && texture.Slice((int)headerLength, PngSignature.Length).SequenceEqual(PngSignature);
            return single && png ? ((int)headerLength, at) : null;
        }

        return null;
    }
}
