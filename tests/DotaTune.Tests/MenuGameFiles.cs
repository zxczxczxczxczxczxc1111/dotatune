using System.Buffers.Binary;
using System.Text;
using DotaTune.Core.Effects;
using SkiaSharp;

namespace DotaTune.Tests;

/// <summary>
/// Menu background files for the fake game, built the way build 6967 keeps them: background
/// layouts, the empty layout, the default background's compiled stylesheet with its image table
/// and a panorama texture stored as PNG.
/// </summary>
internal static class MenuGameFiles
{
    public const string Empty = "panorama/layout/empty.vxml_c";
    public const string DefaultLayout = "panorama/layout/dashboard_background_default.vxml_c";
    public const string DefaultStyle = "panorama/styles/dashboard_background_default.vcss_c";
    public const string Image = "panorama/images/backgrounds/dashboard_background_psd.vtex";
    public const string IngameImage = "panorama/images/backgrounds/dashboard_background_ingame_psd.vtex";

    /// <summary>The rules that show the two images, as the compiled stylesheet keeps them.</summary>
    public const string ImageCss =
        "#BackgroundImage{background-image: url(\"s2r://" + Image + "\");background-size: 100% 100%;}"
        + "DOTADashboard.IsInGame #BackgroundImage{background-image: url(\"s2r://" + IngameImage + "\");}";

    public const string SceneCss = "#BackgroundMap{blur: gaussian( 2 );max-width: 100%;width: 50%;height: 50%;opacity: 0;}#BackgroundMap.SceneLoaded{opacity: 1;}";

    /// <summary>Every layout of <see cref="All"/> that is a background and not a page, in the order the reader sorts them.</summary>
    public static readonly string[] Backgrounds =
    [
        DefaultLayout,
        "panorama/layout/dashboard_background_greenscreen.vxml_c",
        "panorama/layout/dashboard_background_monster_hoard_2026.vxml_c",
    ];

    /// <summary>The layouts of <see cref="All"/> named as backgrounds that are pages, sorted.</summary>
    public static readonly string[] Pages =
    [
        "panorama/layout/dashboard_background_loadout_treasure.vxml_c",
        "panorama/layout/dashboard_background_seasonal_reward_line_fall_2026.vxml_c",
        "panorama/layout/events/frostivus_2025/frostivus_background.vxml_c",
    ];

    /// <summary>The advert the home page of build 6969 shows: item previews and a button that opens the store.</summary>
    public static byte[] AdvertTree() => Layout(Panel(
        "Panel",
        [],
        Panel("DOTAParticleScenePanel", []),
        Panel("DOTAUIEconSetPreview", [("id", "PremiumReward_Left")]),
        Panel("Image", []),
        Panel("Button", [("onactivate", "DOTAShowTreasurePage( 1 );")], Panel("Label", [("text", "#Buy")]))));

    /// <summary>The default background of build 6969: a picture under a 3D scene, with a handler that runs on load.</summary>
    public static byte[] DefaultTree() => Layout(Panel(
        "Panel",
        [("class", "DashboardBackgroundDefault")],
        Panel(
            "Panel",
            [("id", "DashboardBackgroundInitContainer")],
            Panel("Panel", [("id", "BackgroundImage")]),
            Panel("DOTAScenePanel", [("id", "BackgroundMap"), ("map", "backgrounds/dashboard_parallax_test"), ("onload", "Init()")]),
            Panel("Image", [("id", "BackgroundVignette")]),
            Panel("Label", [("text", "#DOTA_Title")]))));

    /// <summary>The home page of build 6969: the column of event tiles in the right column, or a page without it.</summary>
    public static byte[] HomeTree(bool withColumn = true) => Layout(Panel(
        "DOTAHomePage",
        [("class", "DashboardPage"), ("hittest", "false")],
        Panel("Panel", [("id", "StatLine")], Panel("DOTAAntiAddictionBadge", [("class", "StatLineContainer")])),
        Panel(
            "Panel",
            [("id", "TodayPages")],
            Panel("Panel", [("id", "RightColumn"), ("hittest", "false")], Panel("DOTAFrontpageContents", [("id", withColumn ? "FrontpageContents" : "OtherContents")])))));

    /// <summary>The treasure preview of build 6969: a panel of the game's own kind that finds BackgroundScene in itself.</summary>
    public static byte[] TreasureTree() => Layout(Panel(
        "DOTACCBackgroundSummer2023",
        [("class", "BackgroundPanel")],
        Panel("DOTAScenePanel", [("id", "BackgroundScene"), ("onload", "RunPageAnimation()")])));

    /// <summary>A compiled layout: the resource header and a LaCo block holding the tree with the given root panel.</summary>
    public static byte[] Layout(Kv3Value panel)
    {
        Kv3Value root = Node("ROOT", null, [Node("STYLES", null, [Node("INCLUDE", null, [])]), panel]);
        Kv3Value ast = Kv3Value.Object(Kv3Flag.None, [new Kv3Member("m_pRoot", root)]);
        Kv3Value document = Kv3Value.Object(Kv3Flag.None, [new Kv3Member("m_AST", ast)]);
        return WithBlocks(("LaCo", Kv3Binary.Write(new Kv3Document(Guid.Empty, document))), ("DATA", new byte[8]));
    }

    /// <summary>A panel node with its attributes first and then its panels, as the compiler writes them.</summary>
    public static Kv3Value Panel(string kind, IReadOnlyList<(string Name, string Value)> attributes, params Kv3Value[] panels) =>
        Node("PANEL", kind, [.. attributes.Select(a => Node("PANEL_ATTRIBUTE", a.Name, [Node("PANEL_ATTRIBUTE_VALUE", a.Value, [])])), .. panels]);

    /// <summary>A node: no children, one child under "child", or several under "vecChildren", as the compiler stores them.</summary>
    public static Kv3Value Node(string type, string? name, IReadOnlyList<Kv3Value> children)
    {
        List<Kv3Member> members = [new("eType", Kv3Value.String(Kv3Flag.None, type))];
        if (name is not null)
        {
            members.Add(new("name", Kv3Value.String(Kv3Flag.None, name)));
        }

        if (children.Count == 1)
        {
            members.Add(new("child", children[0]));
        }
        else if (children.Count > 1)
        {
            members.Add(new("vecChildren", Kv3Value.Array(Kv3Type.Array, Kv3Flag.None, [.. children])));
        }

        return Kv3Value.Object(Kv3Flag.None, members);
    }

    /// <summary>
    /// The menu files of a game: the default stylesheet is <see cref="ImageCss"/> and then the given
    /// text, with or without the empty layout. The menu image is a compressed texture, as in build
    /// 6967, so the picture is modelled on the PNG texture after it.
    /// </summary>
    public static Dictionary<string, byte[]> All(string css = SceneCss, bool withEmpty = true)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [DefaultLayout] = DefaultTree(),
            ["panorama/layout/dashboard_background_greenscreen.vxml_c"] = Layout(Panel("Panel", [("class", "DashboardBackgroundGreenscreen")])),
            ["panorama/layout/dashboard_background_loadout_treasure.vxml_c"] = TreasureTree(),
            ["panorama/layout/dashboard_background_monster_hoard_2026.vxml_c"] = AdvertTree(),
            ["panorama/layout/dashboard_background_seasonal_reward_line_fall_2026.vxml_c"] = Layout(Panel(
                "Panel", [("class", "SeasonalRewardLineBackground")], Panel("DOTAFrontpageSeasonalRewards", [("id", "SeasonalRewardsDB")]))),

            // A layout of an older shape, without the tree, is not taken for a picture.
            ["panorama/layout/events/frostivus_2025/frostivus_background.vxml_c"] = GameFiles.Resource(),
            ["panorama/layout/dashboard_background_manager.vxml_c"] = GameFiles.Resource(),
            ["panorama/layout/events/frostivus_2025/frostivus_shop.vxml_c"] = GameFiles.Resource(),
            [DefaultStyle] = Style([("panorama/images/backgrounds/other_psd.vtex", 64, 64), (Image, 240, 135), (IngameImage, 240, 135)], ImageCss + css),
            ["panorama/images/backgrounds/a_rgba_psd.vtex_c"] = GameFiles.Texture(4, 4),
            [Image + "_c"] = GameFiles.Texture(8, 8, format: 2),
            ["panorama/images/backgrounds/glow_png.vtex_c"] = PngTexture(Png(160, 90)),
        };
        if (withEmpty)
        {
            files[Empty] = GameFiles.Resource("panorama/styles/base.vcss");
        }

        return files;
    }

    /// <summary>Puts the menu files next to the default content of the fake game's pak01.</summary>
    public static void AddTo(FakeSteam steam, Dictionary<string, byte[]>? menu = null)
    {
        Dictionary<string, byte[]> files = GameFiles.Default();
        foreach ((string path, byte[] data) in menu ?? All())
        {
            files[path] = data;
        }

        GameFiles.WritePak(steam.Pak01, files);
    }

    /// <summary>A PNG file of one colour.</summary>
    public static byte[] Png(int width, int height, SKColor? colour = null)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(colour ?? new SKColor(200, 40, 40));
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A compiled stylesheet: header, one DATA block of CRC, the image table and the CSS text.</summary>
    public static byte[] Style(IReadOnlyList<(string Name, int Width, int Height)> images, string css)
    {
        var data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(0x12345678u));
        data.AddRange(BitConverter.GetBytes((ushort)images.Count));
        foreach ((string name, int width, int height) in images)
        {
            data.AddRange(Encoding.ASCII.GetBytes(name + "\0"));
            data.AddRange(BitConverter.GetBytes((ushort)width));
            data.AddRange(BitConverter.GetBytes((ushort)height));
            data.AddRange(BitConverter.GetBytes(0xCAFEu));
        }

        data.AddRange(Encoding.ASCII.GetBytes(css));
        return WithHeader([.. data]);
    }

    /// <summary>A panorama texture of format PNG_RGBA8888: the header with one DATA block, then the PNG.</summary>
    public static byte[] PngTexture(byte[] png)
    {
        byte[] header = WithHeader(new byte[40]);
        int data = 16 + 12;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(data), 1);
        (int width, int height) = (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(data + 20), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(data + 22), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(data + 24), 1);
        header[data + 26] = 16;
        header[data + 27] = 1;
        return [.. header, .. png];
    }

    /// <summary>
    /// The resource header and a block table with one DATA block holding <paramref name="data"/>.
    /// The first field is the file length, or for a texture the length of the header, which the
    /// image then follows.
    /// </summary>
    private static byte[] WithHeader(byte[] data) => WithBlocks(("DATA", data));

    /// <summary>The resource header, a block table and the blocks one after another in its order.</summary>
    private static byte[] WithBlocks(params (string Name, byte[] Data)[] blocks)
    {
        const int Table = 16;
        int at = Table + (blocks.Length * 12);
        var bytes = new byte[at + blocks.Sum(b => b.Data.Length)];
        Span<byte> span = bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 12);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Table - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)blocks.Length);
        for (int i = 0; i < blocks.Length; i++)
        {
            int entry = Table + (i * 12);
            Encoding.ASCII.GetBytes(blocks[i].Name).CopyTo(span[entry..]);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(entry + 4)..], (uint)(at - (entry + 4)));
            BinaryPrimitives.WriteUInt32LittleEndian(span[(entry + 8)..], (uint)blocks[i].Data.Length);
            blocks[i].Data.CopyTo(span[at..]);
            at += blocks[i].Data.Length;
        }

        return bytes;
    }
}
