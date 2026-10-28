using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DotaTune.Core.Effects;
using DotaTune.Core.Menu;
using DotaTune.Core.Mmr;
using DotaTune.Core.Platform;
using DotaTune.Resources;
using DotaTune.ViewModels;
using DotaTune.Views;
using ValvePak;

namespace DotaTune.Tests;

public class MenuBackgroundPageTests
{
    /// <summary>The game's own background, black and the samples: the tiles before anything is added.</summary>
    private static readonly int PlainTiles = 2 + MenuPictures.Samples.Count;

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Black_background_goes_into_the_pack(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        MenuGameFiles.AddTo(steam);
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, language);

        Assert.Equal(Strings.MenuGame, page.Tiles[0].Title);
        Assert.Equal([null, MenuPictures.BlackId, .. MenuPictures.Samples.Select(MenuPictures.SampleId)], page.Tiles.Select(t => t.Id));
        Assert.All(page.Tiles.Skip(2), t => Assert.NotNull(t.Preview));
        Assert.Equal([true, true, .. MenuPictures.Samples.Select(_ => false)], page.Tiles.Select(t => t.ShowsTitle));
        Assert.Same(page.Tiles[0], page.Selected);
        Assert.Equal(Strings.MenuNoPack, page.Summary);
        Assert.False(page.CanApplyNow);

        page.Selected = page.Tiles[1];
        Assert.Equal(MenuPictures.BlackId, App.Services.Settings.Load().MenuBackground);
        Assert.True(page.ApplyCommand.CanExecute(null));
        Assert.Equal(Strings.InstallPack, page.ApplyText);

        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.MenuPackMatches, page.Summary);
        Assert.True(page.IsOk);
        Assert.False(page.CanApplyNow);
        Assert.Equal(MenuPictures.BlackId, App.Services.Packs.Current!.MenuBackground);
        byte[] empty = MenuGameFiles.All()[MenuGameFiles.Empty];
        Assert.All(MenuGameFiles.Backgrounds, layout => Assert.Equal(empty, ReadFromPack(layout)));
        Assert.All(MenuGameFiles.Pages, page => Assert.Null(ReadFromPack(page)));

        // Back to the game's own background: the pack has to be built again, and then holds no layout.
        page.Selected = page.Tiles[0];
        Assert.Equal(Strings.MenuPackDiffers, page.Summary);
        Assert.True(page.IsAttention);
        Assert.Equal(Strings.RebuildPack, page.ApplyText);
        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.MenuPackMatches, page.Summary);
        Assert.Null(App.Services.Packs.Current!.MenuBackground);
        Assert.Null(ReadFromPack(MenuGameFiles.Backgrounds[1]));

        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Own_picture_is_added_installed_and_removed(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        MenuGameFiles.AddTo(steam);
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, language);
        string file = Path.Combine(folder.Path, "Мои картинки", "паук.png");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, MenuGameFiles.Png(2560, 1440));
        page.PickPicturePath = () => Task.FromResult<string?>(file);

        page.AddPictureCommand.Execute(null);
        Settle(page);
        MenuTile added = page.Tiles[^1];
        Assert.Same(added, page.Selected);
        Assert.True(added.IsAdded);
        Assert.Equal(Strings.MenuOwnPicture, added.Title);
        Assert.NotNull(added.Preview);
        Assert.True(page.CanRemove);
        Assert.Equal(added.Id, App.Services.Settings.Load().MenuBackground);

        // The same file again picks the tile that is there.
        page.Selected = page.Tiles[1];
        page.AddPictureCommand.Execute(null);
        Settle(page);
        Assert.Same(added, page.Selected);
        Assert.Equal(PlainTiles + 1, page.Tiles.Count);

        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.MenuPackMatches, page.Summary);
        Assert.Equal(added.Id, App.Services.Packs.Current!.MenuBackground);
        byte[] texture = ReadFromPack(GameMenuBackground.PictureImages[0].Picture + "_c")!;
        Assert.Equal(App.Services.MenuPictures.Png(added.Id!), texture[^App.Services.MenuPictures.Png(added.Id!).Length..]);
        Assert.NotNull(ReadFromPack(MenuGameFiles.DefaultStyle));

        page.RemovePictureCommand.Execute(null);
        Assert.Same(page.Tiles[0], page.Selected);
        Assert.Equal(PlainTiles, page.Tiles.Count);
        Assert.Empty(App.Services.MenuPictures.Added());
        Assert.Null(App.Services.Settings.Load().MenuBackground);
        Assert.True(File.Exists(file));
        Assert.Equal(Strings.MenuPackDiffers, page.Summary);

        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaFact]
    public void Sample_goes_into_the_pack_as_png()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        MenuGameFiles.AddTo(steam);
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, "ru");
        MenuTile sample = page.Tiles[2];
        page.Selected = sample;

        page.ApplyCommand.Execute(null);
        Settle(page);

        Assert.Equal(Strings.MenuPackMatches, page.Summary);
        Assert.Equal(sample.Id, App.Services.Packs.Current!.MenuBackground);
        byte[] png = App.Services.MenuPictures.Png(sample.Id!);
        byte[] texture = ReadFromPack(GameMenuBackground.PictureImages[1].Picture + "_c")!;
        Assert.Equal(png, texture[^png.Length..]);
        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaFact]
    public void Picture_that_is_not_an_image_is_reported()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, "ru");
        string file = Path.Combine(folder.Path, "текст.png");
        File.WriteAllText(file, "не картинка");
        page.PickPicturePath = () => Task.FromResult<string?>(file);

        page.AddPictureCommand.Execute(null);
        Settle(page);

        Assert.Equal(Strings.MenuPictureUnreadable, page.Note);
        Assert.Equal(PlainTiles, page.Tiles.Count);
        Assert.Same(page.Tiles[0], page.Selected);
        window.Close();
    }

    [AvaloniaFact]
    public void Game_that_cannot_show_a_picture_offers_black_only()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        MenuGameFiles.AddTo(steam, MenuGameFiles.All("#BackgroundMap.SceneLoaded{opacity: 1;}"));
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, "ru");

        page.Selected = page.Tiles[2];
        Assert.Equal(Strings.MenuPictureNotSupported, page.Note);
        Assert.False(page.CanApplyNow);

        page.Selected = page.Tiles[1];
        Assert.Null(page.Note);
        Assert.True(page.CanApplyNow);
        window.Close();
    }

    [AvaloniaFact]
    public void Game_without_menu_files_lets_nothing_but_its_own_background()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, "ru");

        page.Selected = page.Tiles[1];
        Assert.Equal(Strings.MenuNotSupported, page.Note);
        Assert.False(page.CanApplyNow);
        window.Close();
    }

    [AvaloniaFact]
    public void Background_left_out_of_the_pack_is_reported()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        MenuGameFiles.AddTo(steam);
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, "ru");
        page.Selected = page.Tiles[1];

        // The game changes between opening the page and building the pack.
        MenuGameFiles.AddTo(steam, MenuGameFiles.All(withEmpty: false));
        page.ApplyCommand.Execute(null);
        Settle(page);

        Assert.Equal(Strings.MenuNotApplied, page.Note);
        Assert.Null(App.Services.Packs.Current!.MenuBackground);
        Assert.Equal(Strings.MenuPackDiffers, page.Summary);
        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Hidden_events_go_into_the_pack_and_come_out(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        MenuGameFiles.AddTo(steam, WithMenuScripts());
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, language);
        Assert.False(page.HideEvents);
        Assert.False(page.CanApplyNow);

        page.HideEvents = true;
        Assert.True(App.Services.Settings.Load().HideHomeEvents);
        Assert.Null(page.Note);
        Assert.True(page.CanApplyNow);

        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.MenuPackMatchesWithoutEvents, page.Summary);
        Assert.True(page.IsOk);
        Assert.False(page.CanApplyNow);
        Assert.True(App.Services.Packs.Current!.HideEvents);
        Assert.False(App.Services.Packs.Current!.Mmr);
        Assert.Contains(HomeEvents.ColumnId, ScriptText(MmrMenu.EventsPath), StringComparison.Ordinal);
        Assert.StartsWith("// DotaTune", ScriptText(MmrMenu.ScriptPath), StringComparison.Ordinal);
        Assert.NotNull(ReadFromPack(MmrMenu.BasePath));

        // The switch off: the pack holds the events hidden until it is built again, then no menu script at all.
        page.HideEvents = false;
        Assert.Equal(Strings.MenuPackEventsHidden, page.Summary);
        Assert.True(page.IsAttention);
        Assert.True(page.CanApplyNow);
        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.MenuPackMatches, page.Summary);
        Assert.False(App.Services.Packs.Current!.HideEvents);
        Assert.Null(ReadFromPack(MmrMenu.BasePath));
        Assert.Null(ReadFromPack(MmrMenu.EventsPath));

        page.HideEvents = true;
        Assert.Equal(Strings.MenuPackEventsShown, page.Summary);
        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaFact]
    public void Hidden_events_and_the_mmr_line_go_in_together()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        MenuGameFiles.AddTo(steam, WithMenuScripts());
        App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);
        App.Services.Settings.Save(App.Services.Settings.Load() with { MmrLine = true, HideHomeEvents = true });
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, "ru", keepServices: true);
        Assert.True(page.HideEvents);

        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.True(App.Services.Packs.Current!.HideEvents);
        Assert.True(App.Services.Packs.Current!.Mmr);
        Assert.Contains(HomeEvents.ColumnId, ScriptText(MmrMenu.EventsPath), StringComparison.Ordinal);
        Assert.Contains("DOTATUNE_MMR", ScriptText(MmrMenu.ScriptPath), StringComparison.Ordinal);
        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaFact]
    public void Game_without_the_event_column_keeps_its_events()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        MenuGameFiles.AddTo(steam, WithMenuScripts(withColumn: false));
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, "ru");

        page.HideEvents = true;
        Assert.Equal(Strings.MenuEventsNotSupported, page.Note);
        Assert.False(page.CanApplyNow);

        // With a background to put in the pack goes in without the script.
        page.Selected = page.Tiles[1];
        Assert.Equal(Strings.MenuEventsNotSupported, page.Note);
        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.MenuPackMatches, page.Summary);
        Assert.False(App.Services.Packs.Current!.HideEvents);
        Assert.Null(ReadFromPack(MmrMenu.EventsPath));
        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaFact]
    public void Game_with_another_root_menu_keeps_its_events()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        Dictionary<string, byte[]> files = WithMenuScripts();
        // A root layout that already loads scripts is not the one DotaTune knows.
        files[MmrMenu.BasePath] = MmrMenu.PackFiles(null, "ru", hideEvents: false)[MmrMenu.BasePath];
        MenuGameFiles.AddTo(steam, files);
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, "ru");

        page.HideEvents = true;
        Assert.Equal(Strings.MenuEventsNotSupported, page.Note);
        Assert.False(page.CanApplyNow);
        window.Close();
    }

    [AvaloniaFact]
    public void Picture_the_game_cannot_show_does_not_hold_back_the_events()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        Dictionary<string, byte[]> files = WithMenuScripts();
        files[MenuGameFiles.DefaultStyle] = MenuGameFiles.All("#BackgroundMap.SceneLoaded{opacity: 1;}")[MenuGameFiles.DefaultStyle];
        MenuGameFiles.AddTo(steam, files);
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, "ru");
        page.Selected = page.Tiles[2];
        Assert.Equal(Strings.MenuPictureNotSupported, page.Note);
        Assert.False(page.CanApplyNow);

        page.HideEvents = true;
        Assert.True(page.CanApplyNow);
        page.ApplyCommand.Execute(null);
        Settle(page);

        Assert.True(App.Services.Packs.Current!.HideEvents);
        Assert.Null(App.Services.Packs.Current!.MenuBackground);
        Assert.Equal(Strings.MenuPictureNotSupported, page.Note);
        Assert.Equal(Strings.MenuPackDiffers, page.Summary);
        Assert.False(page.CanApplyNow);

        // A pack that is gone has to be built again, the picture still left out.
        File.Delete(App.Services.Packs.Current!.PakPath);
        page.Activated();
        Settle(page);
        Assert.Equal(Strings.HealthMissing, page.Summary);
        Assert.True(page.CanApplyNow);
        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaFact]
    public void Switch_turned_off_while_the_pack_is_built_is_not_a_failure()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        MenuGameFiles.AddTo(steam, WithMenuScripts());
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, "ru");
        page.HideEvents = true;
        // The build reads the switch turned off, whenever the page turns it off.
        App.Services.Settings.Save(App.Services.Settings.Load() with { HideHomeEvents = false });

        page.ApplyCommand.Execute(null);
        page.HideEvents = false;
        Settle(page);

        Assert.False(App.Services.Packs.Current!.HideEvents);
        Assert.NotEqual(Strings.MenuEventsNotApplied, page.Note);
        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaFact]
    public void Event_column_gone_before_the_build_is_reported()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        MenuGameFiles.AddTo(steam, WithMenuScripts());
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, "ru");
        page.HideEvents = true;

        // The game changes between opening the page and building the pack.
        MenuGameFiles.AddTo(steam, WithMenuScripts(withColumn: false));
        page.ApplyCommand.Execute(null);
        Settle(page);

        Assert.Equal(Strings.MenuEventsNotApplied, page.Note);
        Assert.False(App.Services.Packs.Current!.HideEvents);
        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaFact]
    public void Saved_picture_that_is_gone_falls_back_to_the_game()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);
        App.Services.Settings.Save(App.Services.Settings.Load() with { MenuBackground = "picture:0123456789abcdef.png" });
        (MainWindow window, MenuBackgroundViewModel page) = Open(folder, steam, "ru", keepServices: true);

        Assert.Same(page.Tiles[0], page.Selected);
        Assert.Null(App.Services.Settings.Load().MenuBackground);
        window.Close();
    }

    private static (MainWindow, MenuBackgroundViewModel) Open(CyrillicTempFolder folder, FakeSteam steam, string language, bool keepServices = false)
    {
        if (!keepServices)
        {
            App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);
        }

        UiLanguage.Apply(UiLanguage.Resolve(language));
        MainWindow window = App.CreateMainWindow(SectionKind.MenuBackground);
        window.Show();
        var main = (MainViewModel)window.DataContext!;
        var page = (MenuBackgroundViewModel)main.Sections.Single(s => s.Kind == SectionKind.MenuBackground).Page;
        Settle(page);
        return (window, page);
    }

    /// <summary>The menu files plus the root layout of the game and its home page, which the menu scripts need.</summary>
    private static Dictionary<string, byte[]> WithMenuScripts(bool withColumn = true)
    {
        Dictionary<string, byte[]> files = MenuGameFiles.All();
        files[MmrMenu.BasePath] = MmrTests.ValveBase();
        files[HomeEvents.HomePath] = MenuGameFiles.HomeTree(withColumn);
        return files;
    }

    private static string ScriptText(string path) => Encoding.UTF8.GetString(CompiledResource.Data(ReadFromPack(path)!));

    /// <summary>A file of the installed pack, or null when the pack does not have it.</summary>
    private static byte[]? ReadFromPack(string path)
    {
        using var package = new Package();
        package.Read(App.Services.Packs.Current!.PakPath);
        if (package.FindEntry(path) is not { } entry)
        {
            return null;
        }

        package.ReadEntry(entry, out byte[] data);
        return data;
    }

    private static void Settle(MenuBackgroundViewModel page)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (page.IsBusy && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.False(page.IsBusy, "the menu background page did not settle in 10 s");
    }
}
