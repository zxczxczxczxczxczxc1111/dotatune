using System.Buffers.Binary;
using System.Text;
using DotaTune.Core.Menu;

namespace DotaTune.Tests;

public sealed class MenuBackgroundTests : IDisposable
{
    private readonly FakeSteam _steam = new();

    public void Dispose() => _steam.Dispose();

    [Theory]
    [InlineData("panorama/layout/dashboard_background_default.vxml_c", true)]
    [InlineData("panorama/layout/dashboard_background_monster_hoard_2026.vxml_c", true)]
    [InlineData("panorama/layout/events/frostivus_2025/frostivus_background.vxml_c", true)]
    [InlineData("panorama/layout/events/crownfall/act_1/background_map.vxml_c", true)]
    [InlineData("panorama/layout/dashboard_background_manager.vxml_c", false)]
    [InlineData("panorama/layout/dashboard_page_home.vxml_c", false)]
    [InlineData("panorama/layout/events/frostivus_2025/frostivus_shop.vxml_c", false)]
    [InlineData("panorama/layout/dashboard_background_default.vxml", false)]
    [InlineData("panorama/styles/dashboard_background_default.vcss_c", false)]
    public void Background_layouts_are_told_by_name(string path, bool expected) =>
        Assert.Equal(expected, GameMenuBackground.IsBackgroundLayout(path));

    [Fact]
    public void Game_files_give_the_backgrounds_and_the_picture_model()
    {
        MenuGameFiles.AddTo(_steam);

        GameMenuBackground menu = GameMenuBackground.Read(DotaFolder);

        Assert.Equal(MenuGameFiles.Backgrounds, menu.Layouts);
        Assert.Equal(MenuGameFiles.Pages, menu.Pages);
        Assert.Equal(MenuGameFiles.All()[MenuGameFiles.Empty], menu.EmptyLayout);
        Assert.True(menu.CanShowPicture);
        // The first textures of the folder are RGBA and compressed, so the model is the PNG after them.
        Assert.Equal(MenuGameFiles.All()["panorama/images/backgrounds/glow_png.vtex_c"], menu.PictureTemplate);
    }

    [Fact]
    public void Game_without_the_empty_layout_or_backgrounds_is_refused()
    {
        MenuGameFiles.AddTo(_steam, MenuGameFiles.All(withEmpty: false));
        Assert.Throws<InvalidDataException>(() => GameMenuBackground.Read(DotaFolder));

        Dictionary<string, byte[]> noBackgrounds = MenuGameFiles.All();
        foreach (string layout in MenuGameFiles.Backgrounds.Concat(MenuGameFiles.Pages))
        {
            noBackgrounds.Remove(layout);
        }

        MenuGameFiles.AddTo(_steam, noBackgrounds);
        Assert.Throws<InvalidDataException>(() => GameMenuBackground.Read(DotaFolder));
    }

    [Fact]
    public void Game_with_pages_only_can_show_no_background()
    {
        Dictionary<string, byte[]> pagesOnly = MenuGameFiles.All();
        foreach (string layout in MenuGameFiles.Backgrounds)
        {
            pagesOnly.Remove(layout);
        }

        MenuGameFiles.AddTo(_steam, pagesOnly);
        GameMenuBackground menu = GameMenuBackground.Read(DotaFolder);

        Assert.Empty(menu.Layouts);
        Assert.Equal(MenuGameFiles.Pages, menu.Pages);
        Assert.False(menu.CanShowBlack);
        Assert.False(menu.CanShowPicture);
        Assert.Throws<InvalidOperationException>(() => menu.PackFiles(MenuBackgroundKind.Black));
        Assert.Throws<InvalidOperationException>(() => menu.PackFiles(MenuBackgroundKind.Picture, MenuGameFiles.Png(16, 9)));
    }

    [Fact]
    public void Default_background_that_became_a_page_takes_no_picture()
    {
        Dictionary<string, byte[]> files = MenuGameFiles.All();
        files[MenuGameFiles.DefaultLayout] = MenuGameFiles.TreasureTree();
        MenuGameFiles.AddTo(_steam, files);
        GameMenuBackground menu = GameMenuBackground.Read(DotaFolder);

        Assert.True(menu.CanShowBlack);
        Assert.False(menu.CanShowPicture);
        Assert.Throws<InvalidOperationException>(() => menu.PackFiles(MenuBackgroundKind.Picture, MenuGameFiles.Png(16, 9)));
        Assert.DoesNotContain(MenuGameFiles.DefaultLayout, menu.PackFiles(MenuBackgroundKind.Black).Keys);
    }

    [Fact]
    public void Game_whose_layouts_are_all_unreadable_is_refused()
    {
        Dictionary<string, byte[]> files = MenuGameFiles.All();
        foreach (string layout in MenuGameFiles.Backgrounds.Concat(MenuGameFiles.Pages))
        {
            files[layout] = GameFiles.Resource();
        }

        MenuGameFiles.AddTo(_steam, files);
        Assert.Throws<InvalidDataException>(() => GameMenuBackground.Read(DotaFolder));
    }

    public static TheoryData<string, byte[], LayoutKind> Layouts => new()
    {
        { "the default picture under a scene", MenuGameFiles.DefaultTree(), LayoutKind.Background },
        { "an empty box", MenuGameFiles.Layout(MenuGameFiles.Panel("Panel", [])), LayoutKind.Background },
        { "an advert with a button to the store", MenuGameFiles.AdvertTree(), LayoutKind.Background },
        { "a hero debut with a hover handler and a text button", MenuGameFiles.Layout(MenuGameFiles.Panel("Panel", [("onmouseover", "Look()")], MenuGameFiles.Panel("TextButton", [("onactivate", "Go()")]))), LayoutKind.Background },
        { "an advert with a button of its own kind", MenuGameFiles.Layout(MenuGameFiles.Panel("Panel", [], MenuGameFiles.Panel("DOTAMonsterHoard2027Button", []))), LayoutKind.Background },
        { "the treasure preview, a panel of the game's own kind", MenuGameFiles.TreasureTree(), LayoutKind.Page },
        {
            "the seasonal event deep inside",
            MenuGameFiles.Layout(MenuGameFiles.Panel(
                "Panel", [], MenuGameFiles.Panel("Panel", [], MenuGameFiles.Panel("DOTAFrontpageSeasonalRewards", [], MenuGameFiles.Panel("DOTAEventRewardClaimButton", []))))),
            LayoutKind.Page
        },
        { "a reward claim button alone", MenuGameFiles.Layout(MenuGameFiles.Panel("Panel", [], MenuGameFiles.Panel("DOTAEventRewardClaimButton", []))), LayoutKind.Page },
        { "the map update with a carousel", MenuGameFiles.Layout(MenuGameFiles.Panel("Panel", [], MenuGameFiles.Panel("Carousel", []))), LayoutKind.Page },
        { "a node of an unknown kind", MenuGameFiles.Layout(MenuGameFiles.Panel("Panel", [], MenuGameFiles.Node("SNIPPET_INSTANCE", "Reward", []))), LayoutKind.Page },
        { "a panel without a kind", MenuGameFiles.Layout(MenuGameFiles.Node("PANEL", null, [])), LayoutKind.Page },
        { "no panel at all", MenuGameFiles.Layout(MenuGameFiles.Node("SCRIPTS", null, [])), LayoutKind.Page },
        { "no tree", GameFiles.Resource(), LayoutKind.Unreadable },
        { "not a resource", "<root><Panel /></root>"u8.ToArray(), LayoutKind.Unreadable },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Layout_is_a_background_only_when_made_of_background_panels(string what, byte[] layout, LayoutKind expected)
    {
        Assert.NotEmpty(what);
        Assert.Equal(expected, PanoramaLayout.Kind(layout));
    }

    [Fact]
    public void Black_puts_the_empty_layout_under_every_background()
    {
        MenuGameFiles.AddTo(_steam);
        GameMenuBackground menu = GameMenuBackground.Read(DotaFolder);

        Dictionary<string, byte[]> files = menu.PackFiles(MenuBackgroundKind.Black);

        Assert.Equal(MenuGameFiles.Backgrounds.Order(StringComparer.Ordinal), files.Keys.Order(StringComparer.Ordinal));
        Assert.All(files.Values, v => Assert.Equal(menu.EmptyLayout, v));
        Assert.Empty(menu.PackFiles(MenuBackgroundKind.Game));
        Assert.All(MenuGameFiles.Pages, page => Assert.DoesNotContain(page, files.Keys));
    }

    [Fact]
    public void Picture_goes_in_under_its_own_names_with_its_size_and_without_the_scene()
    {
        MenuGameFiles.AddTo(_steam);
        GameMenuBackground menu = GameMenuBackground.Read(DotaFolder);
        byte[] png = MenuGameFiles.Png(1920, 1080);

        Dictionary<string, byte[]> files = menu.PackFiles(MenuBackgroundKind.Picture, png);

        foreach (string layout in MenuGameFiles.Backgrounds.Skip(1))
        {
            Assert.Equal(menu.DefaultLayout, files[layout]);
        }

        Assert.False(files.ContainsKey(MenuGameFiles.DefaultLayout));
        Assert.All(MenuGameFiles.Pages, page => Assert.DoesNotContain(page, files.Keys));
        foreach ((string image, string picture) in GameMenuBackground.PictureImages)
        {
            // The game's own image stays: the screen before loading shows it too.
            Assert.False(files.ContainsKey(image + "_c"));
            byte[] texture = files[picture + "_c"];
            Assert.True(PanoramaTexture.IsPngTexture(texture));
            Assert.Equal(png, texture[^png.Length..]);
            Assert.Equal(1920, BinaryPrimitives.ReadUInt16LittleEndian(texture.AsSpan(28 + 20)));
            Assert.Equal(1080, BinaryPrimitives.ReadUInt16LittleEndian(texture.AsSpan(28 + 22)));
        }

        // Every change keeps the length, so no offset in the compiled file moves.
        byte[] style = files[MenuGameFiles.DefaultStyle];
        Assert.Equal(menu.DefaultStyle!.Length, style.Length);
        string css = Encoding.ASCII.GetString(style);
        Assert.Contains("#BackgroundMap{blur: gaussian( 2 );max-width: 100%;width: 0px;height: 0px;opacity: 0;}", css, StringComparison.Ordinal);
        Assert.Contains($"url(\"s2r://{GameMenuBackground.PictureImages[0].Picture}\")", css, StringComparison.Ordinal);
        Assert.Contains($"url(\"s2r://{GameMenuBackground.PictureImages[1].Picture}\")", css, StringComparison.Ordinal);
        Assert.DoesNotContain("_psd.vtex\"", css, StringComparison.Ordinal);
        Assert.Equal(
            [
                ("panorama/images/backgrounds/other_psd.vtex", 64, 64),
                (GameMenuBackground.PictureImages[0].Picture, 1920, 1080),
                (GameMenuBackground.PictureImages[1].Picture, 1920, 1080),
            ],
            Images(style));
    }

    [Fact]
    public void Picture_names_must_be_as_long_as_the_images()
    {
        byte[] style = MenuGameFiles.All()[MenuGameFiles.DefaultStyle];
        Assert.Throws<ArgumentException>(() => PanoramaStyle.WithPicture(style, [(MenuGameFiles.Image, "panorama/x.vtex")], 16, 9));
    }

    [Fact]
    public void Style_that_does_not_show_an_image_is_not_changed()
    {
        // The image is in the table but no rule shows it.
        byte[] style = MenuGameFiles.Style([(MenuGameFiles.Image, 240, 135), (MenuGameFiles.IngameImage, 240, 135)], MenuGameFiles.SceneCss);
        Assert.Null(PanoramaStyle.WithPicture(style, GameMenuBackground.PictureImages, 1920, 1080));
    }

    [Theory]
    [InlineData("#BackgroundMap.SceneLoaded{opacity: 1;}")]
    [InlineData("#BackgroundMap{blur: gaussian( 2 );opacity: 0;}")]
    [InlineData("#BackgroundMap{width:1;height: 50%;}")]
    public void Picture_is_refused_when_the_scene_rule_is_not_as_expected(string css)
    {
        MenuGameFiles.AddTo(_steam, MenuGameFiles.All(css));
        GameMenuBackground menu = GameMenuBackground.Read(DotaFolder);

        Assert.False(menu.CanShowPicture);
        Assert.Throws<InvalidOperationException>(() => menu.PackFiles(MenuBackgroundKind.Picture, MenuGameFiles.Png(16, 9)));
        Assert.NotEmpty(menu.PackFiles(MenuBackgroundKind.Black));
    }

    [Fact]
    public void Style_without_one_of_the_images_is_not_changed()
    {
        byte[] style = MenuGameFiles.Style([(MenuGameFiles.Image, 240, 135)], MenuGameFiles.ImageCss + MenuGameFiles.SceneCss);
        Assert.Null(PanoramaStyle.WithPicture(style, GameMenuBackground.PictureImages, 1920, 1080));
        Assert.Null(PanoramaStyle.WithPicture(style[..20], GameMenuBackground.PictureImages, 1920, 1080));
    }

    [Fact]
    public void Image_listed_twice_is_renamed_both_times()
    {
        byte[] style = MenuGameFiles.Style(
            [(MenuGameFiles.Image, 240, 135), (MenuGameFiles.IngameImage, 240, 135), (MenuGameFiles.Image, 48, 48)],
            MenuGameFiles.ImageCss + MenuGameFiles.SceneCss);
        byte[] changed = PanoramaStyle.WithPicture(style, GameMenuBackground.PictureImages, 1920, 1080)!;
        Assert.NotNull(changed);
        Assert.Equal(-1, changed.AsSpan().IndexOf(Encoding.ASCII.GetBytes(MenuGameFiles.Image)));
    }

    [Fact]
    public void Texture_and_picture_must_be_png()
    {
        byte[] png = MenuGameFiles.Png(32, 18);
        Assert.Equal((32, 18), PanoramaTexture.PngSize(png));
        Assert.Null(PanoramaTexture.PngSize("not a picture at all, just text"u8));
        Assert.False(PanoramaTexture.IsPngTexture(GameFiles.Texture(4, 4)));

        byte[] template = MenuGameFiles.PngTexture(MenuGameFiles.Png(240, 135));
        Assert.Throws<InvalidDataException>(() => PanoramaTexture.WithPicture(template, [1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => PanoramaTexture.WithPicture(GameFiles.Texture(4, 4), png));
    }

    private string DotaFolder => Path.Combine(_steam.GameRoot, "game", "dota");

    /// <summary>Name, width and height of every image in the table of a compiled stylesheet.</summary>
    private static List<(string, int, int)> Images(byte[] style)
    {
        const int Data = 28;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(style.AsSpan(Data + 4));
        int at = Data + 6;
        var images = new List<(string, int, int)>();
        for (int i = 0; i < count; i++)
        {
            int end = Array.IndexOf(style, (byte)0, at);
            string name = Encoding.ASCII.GetString(style, at, end - at);
            images.Add((name, BinaryPrimitives.ReadUInt16LittleEndian(style.AsSpan(end + 1)), BinaryPrimitives.ReadUInt16LittleEndian(style.AsSpan(end + 3))));
            at = end + 9;
        }

        return images;
    }
}
