using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DotaTune.Core.Effects;
using DotaTune.Core.Platform;
using DotaTune.Resources;
using DotaTune.ViewModels;
using DotaTune.Views;

namespace DotaTune.Tests;

public class EffectsPageTests
{
    [AvaloniaFact]
    public void Choice_of_groups_is_saved()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, MainViewModel main, EffectsViewModel page) = Open(folder, steam);

        Assert.Equal(EffectRules.All, page.Rows.Select(r => r.Group));
        Assert.All(page.Rows, r => Assert.True(r.IsOn));

        Row(page, EffectGroup.Sprays).IsOn = false;
        Assert.Equal(EffectRules.All.Where(g => g != EffectGroup.Sprays), App.Services.Settings.Load().EffectGroups);

        // Every group on again is stored as the list: a group of a later version stays off.
        Row(page, EffectGroup.Sprays).IsOn = true;
        Assert.Equal(EffectRules.All, App.Services.Settings.Load().EffectGroups);
        window.Close();
    }

    [AvaloniaFact]
    public void Pack_is_installed_and_rebuilt_with_the_chosen_groups()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, MainViewModel main, EffectsViewModel page) = Open(folder, steam);

        Assert.Equal(Strings.EffectsNoPack, page.Summary);
        Assert.True(page.ApplyCommand.CanExecute(null));
        Assert.Equal(Strings.InstallPack, page.ApplyText);

        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.EffectsPackMatches, page.Summary);
        Assert.True(page.IsOk);
        Assert.False(page.CanApplyNow);
        Assert.Equal(2, App.Services.Packs.Current!.Effects![EffectGroup.Weather]);

        Row(page, EffectGroup.Weather).IsOn = false;
        Assert.Equal(Strings.EffectsPackDiffers, page.Summary);
        Assert.True(page.IsAttention);
        Assert.Equal(Strings.RebuildPack, page.ApplyText);

        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.EffectsPackMatches, page.Summary);
        Assert.DoesNotContain(EffectGroup.Weather, App.Services.Packs.Current!.Effects!.Keys);
        Assert.Single(Directory.GetFiles(Path.Combine(steam.GameRoot, "game", "dota_russian"), "pak4*_dir.vpk"));

        // The game page reads the game again when it is opened, so it shows the pack put in here.
        var game = (GameStateViewModel)main.Sections.Single(s => s.Kind == SectionKind.GameState).Page;
        Assert.NotEqual(Strings.StatePackReady, game.Summary);
        main.Open(SectionKind.GameState);
        Pump(() => !game.IsLoading);
        Assert.Equal(Strings.StatePackReady, game.Summary);

        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaFact]
    public void Pack_of_an_older_build_or_without_effects_is_offered_for_rebuild()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, MainViewModel main, EffectsViewModel page) = Open(folder, steam);
        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.EffectsPackMatches, page.Summary);

        steam.Write(Path.Combine(steam.GameRoot, "game", "dota", "steam.inf"), "ClientVersion=6968\nServerVersion=6968\nappID=570\n");
        page.Activated();
        Settle(page);
        Assert.Equal(Strings.HealthOtherBuild, page.Summary);
        Assert.True(page.IsAttention);
        Assert.True(page.ApplyCommand.CanExecute(null));

        // A pack from a version without effect groups replaces nothing, so any chosen group differs.
        App.Services.Packs.Install(steam.Read, [0x34, 0x12, 0xAA, 0x55]);
        page.Activated();
        Settle(page);
        Assert.Equal(Strings.EffectsPackDiffers, page.Summary);

        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.EffectsPackMatches, page.Summary);
        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    private static (MainWindow, MainViewModel, EffectsViewModel) Open(CyrillicTempFolder folder, FakeSteam steam)
    {
        App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);
        UiLanguage.Apply(UiLanguage.Resolve("ru"));
        MainWindow window = App.CreateMainWindow(SectionKind.Effects);
        window.Show();
        var main = (MainViewModel)window.DataContext!;
        var page = (EffectsViewModel)main.Sections.Single(s => s.Kind == SectionKind.Effects).Page;
        Settle(page);
        return (window, main, page);
    }

    private static EffectGroupRow Row(EffectsViewModel page, EffectGroup group) => page.Rows.Single(r => r.Group == group);

    private static void Settle(EffectsViewModel page) => Pump(() => !page.IsBusy);

    private static void Pump(Func<bool> done)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), "the effects page did not settle in 10 s");
    }
}
