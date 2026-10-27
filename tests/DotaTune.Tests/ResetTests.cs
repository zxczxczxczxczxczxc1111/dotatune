using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DotaTune.Core.Config;
using DotaTune.Core.HeroGrids;
using DotaTune.Core.Platform;
using DotaTune.Core.Reset;
using DotaTune.Core.Steam;
using DotaTune.Resources;
using DotaTune.ViewModels;
using DotaTune.Views;

namespace DotaTune.Tests;

public class ResetTests
{
    private const string GridPath = "remote/cfg/hero_grid_config.json";
    private const string KeysPath = "remote/cfg/dotakeys_personal.lst";

    [Fact]
    public void Everything_DotaTune_changed_is_taken_back()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-high");
        string keys = steam.WriteGameFile(FakeSteam.AccountId, KeysPath, "keys into");
        steam.WriteGameFile(FakeSteam.OtherAccountId, KeysPath, "keys from");
        string grids = steam.WriteGameFile(FakeSteam.AccountId, GridPath, HeroGridTests.GameFile);
        steam.SetGridSeen();
        AppServices services = Services(folder, steam);
        ChangeEverything(services, steam);
        string pak = Path.Combine(steam.GameRoot, "game", "dota_russian", "pak40_dir.vpk");
        Assert.True(File.Exists(pak));

        Assert.Equal(Enum.GetValues<ResetPart>(), services.Reset.Pending(steam.Read()));
        IReadOnlyList<ResetStep> steps = services.Reset.Run(steam.Read);

        Assert.All(steps, s => Assert.Null(s.Problem));
        Assert.Empty(services.Reset.Pending(steam.Read()));
        Assert.False(File.Exists(pak));
        Assert.Equal("english", steam.Read().Boot!.AudioLanguage);
        Assert.False(File.Exists(AutoexecService.PathIn(steam.GameRoot)));
        Assert.Equal("-high", steam.Read().LaunchOptions);
        Assert.Equal("keys into", File.ReadAllText(keys));
        Assert.Equal(HeroGridTests.GameFile, File.ReadAllText(grids));
    }

    [Fact]
    public void A_transfer_made_after_the_layouts_is_put_back_before_they_are_taken_out()
    {
        // The copy taken before the transfer holds DotaTune's layouts: put back after the layouts
        // were taken out, it would bring them back into the game.
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        string grids = steam.WriteGameFile(FakeSteam.AccountId, GridPath, HeroGridTests.GameFile);
        steam.WriteGameFile(FakeSteam.OtherAccountId, GridPath, HeroGridTests.GameFile);
        steam.SetGridSeen();
        AppServices services = Services(folder, steam);
        HeroGridLayout layout = GridCatalogStore.BuiltIn().Layouts[0];
        Assert.Null(services.HeroGrids.Apply(steam.Read(), FakeSteam.AccountId, [new GridChoice(layout.Name, layout)]).Refusal);
        Assert.Null(services.Transfer.Apply(steam.Read(), FakeSteam.OtherAccountId, FakeSteam.AccountId, [TransferGroup.HeroGrids]).Refusal);

        Assert.All(services.Reset.Run(steam.Read), s => Assert.Null(s.Problem));

        Assert.Equal(HeroGridTests.GameFile, File.ReadAllText(grids));
        Assert.Empty(services.Reset.Pending(steam.Read()));
    }

    [Fact]
    public void Running_game_and_Steam_leave_their_parts_and_say_why()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-high");
        AppServices services = Services(folder, steam);
        Assert.Null(services.Autoexec.Apply(steam.Read(), [new ConfigLine("dota_friendly_color", "0 255 255")]));
        Assert.Null(services.Launch.Apply(steam.Read(), "-high -novid"));
        steam.Running.Add("dota2");
        steam.Running.Add("steam");

        IReadOnlyList<ResetStep> steps = services.Reset.Run(steam.Read);

        Assert.Equal(
            [new ResetStep(ResetPart.Autoexec, ResetProblem.DotaRunning), new ResetStep(ResetPart.LaunchOptions, ResetProblem.SteamRunning)],
            steps);
        Assert.Equal([ResetPart.Autoexec, ResetPart.LaunchOptions], services.Reset.Pending(steam.Read()));
        Assert.True(File.Exists(AutoexecService.PathIn(steam.GameRoot)));
        Assert.Equal("-high -novid", steam.Read().LaunchOptions);
    }

    [Fact]
    public void Nothing_changed_means_nothing_to_do()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.AddPak("russian", 41, withMarker: false);
        AppServices services = Services(folder, steam);

        Assert.Empty(services.Reset.Pending(steam.Read()));
        Assert.Empty(services.Reset.Run(steam.Read));
        Assert.True(File.Exists(Path.Combine(steam.GameRoot, "game", "dota_russian", "pak41_dir.vpk")));
    }

    [Fact]
    public void A_marked_pack_without_a_record_is_taken_out_and_other_mods_stay()
    {
        // The record is lost with the program's data folder, the pack in the game is not.
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.AddPak("russian", 40, withMarker: true);
        steam.AddPak("russian", 41, withMarker: false);
        AppServices services = Services(folder, steam);

        Assert.Equal([ResetPart.Pack], services.Reset.Pending(steam.Read()));
        Assert.All(services.Reset.Run(steam.Read), s => Assert.Null(s.Problem));

        Assert.False(File.Exists(Path.Combine(steam.GameRoot, "game", "dota_russian", "pak40_dir.vpk")));
        Assert.True(File.Exists(Path.Combine(steam.GameRoot, "game", "dota_russian", "pak41_dir.vpk")));
        Assert.Empty(services.Reset.Pending(steam.Read()));
    }

    [Fact]
    public void Copied_settings_changed_since_stay_and_are_reported()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        string keys = steam.WriteGameFile(FakeSteam.AccountId, KeysPath, "keys into");
        steam.WriteGameFile(FakeSteam.OtherAccountId, KeysPath, "keys from");
        AppServices services = Services(folder, steam);
        Assert.Null(services.Transfer.Apply(steam.Read(), FakeSteam.OtherAccountId, FakeSteam.AccountId, [TransferGroup.Hotkeys]).Refusal);
        File.WriteAllText(keys, "keys of the player");

        Assert.Equal([new ResetStep(ResetPart.Transfers, ResetProblem.TransferChangedSince)], services.Reset.Run(steam.Read));

        Assert.Equal("keys of the player", File.ReadAllText(keys));
        Assert.Empty(services.Reset.Pending(steam.Read()));
    }

    [Fact]
    public void Unreadable_localconfig_fails_the_step_instead_of_the_reset()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        // Into the account not signed in: the game state does not read its localconfig.vdf.
        steam.WriteGameFile(FakeSteam.AccountId, KeysPath, "keys from");
        steam.WriteGameFile(FakeSteam.OtherAccountId, KeysPath, "keys into");
        steam.SetLaunchOptions("-novid");
        steam.SetLaunchOptions("-console", FakeSteam.OtherAccountId);
        AppServices services = Services(folder, steam);
        Assert.Null(services.Transfer.Apply(steam.Read(), FakeSteam.AccountId, FakeSteam.OtherAccountId, [TransferGroup.LaunchOptions]).Refusal);
        File.WriteAllText(LaunchOptions.LocalConfigPath(steam.SteamRoot, FakeSteam.OtherAccountId), "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n");

        ResetStep step = Assert.Single(services.Reset.Run(steam.Read), s => s.Part == ResetPart.Transfers);

        Assert.Equal(ResetProblem.Failed, step.Problem);
    }

    [Fact]
    public void Uninstall_takes_the_pack_out_next_to_a_broken_foreign_pak()
    {
        // Another mod manager left a damaged archive in the same folder.
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.AddPak("russian", 41, withMarker: true);
        steam.AddPak("russian", 64, withMarker: false);
        string foreign = Path.Combine(steam.GameRoot, "game", "dota_russian", "pak64_dir.vpk");
        File.WriteAllBytes(foreign, GameStateTests.Damaged(File.ReadAllBytes(foreign), "terminator"));
        AppServices services = Services(folder, steam);

        services.RemoveFromGame();

        Assert.False(File.Exists(Path.Combine(steam.GameRoot, "game", "dota_russian", "pak41_dir.vpk")));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void Uninstall_takes_out_the_same_as_the_page_but_keeps_copied_settings()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.AddPak("russian", 41, withMarker: true);
        string keys = steam.WriteGameFile(FakeSteam.AccountId, KeysPath, "keys into");
        steam.WriteGameFile(FakeSteam.OtherAccountId, KeysPath, "keys from");
        AppServices services = Services(folder, steam);
        Assert.Null(services.Autoexec.Apply(steam.Read(), [new ConfigLine("dota_friendly_color", "0 255 255")]));
        Assert.Null(services.Transfer.Apply(steam.Read(), FakeSteam.OtherAccountId, FakeSteam.AccountId, [TransferGroup.Hotkeys]).Refusal);

        services.RemoveFromGame();

        Assert.False(File.Exists(Path.Combine(steam.GameRoot, "game", "dota_russian", "pak41_dir.vpk")));
        Assert.False(File.Exists(AutoexecService.PathIn(steam.GameRoot)));
        Assert.Equal("keys from", File.ReadAllText(keys));
        Assert.Equal([ResetPart.Transfers], services.Reset.Pending(steam.Read()));
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void The_page_lists_the_changes_asks_once_and_takes_them_back(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-high");
        App.Services = Services(folder, steam);
        Assert.Null(App.Services.InstallPack());
        Assert.Null(App.Services.Autoexec.Apply(steam.Read(), [new ConfigLine("dota_friendly_color", "0 255 255")]));
        (MainWindow window, ResetViewModel page) = Open(steam, language);

        Assert.Equal([Strings.ResetPartPack, Strings.ResetPartAutoexec], page.Rows);
        Assert.True(page.ShowReset);
        Assert.Empty(page.Summary);
        Snapshot(window, $"{language}-Reset-changed.png");

        page.ResetCommand.Execute(null);
        Assert.True(page.IsConfirming);
        Assert.False(page.ShowReset);
        page.CancelCommand.Execute(null);
        Assert.False(page.IsConfirming);
        Assert.True(File.Exists(AutoexecService.PathIn(steam.GameRoot)));

        page.ResetCommand.Execute(null);
        Snapshot(window, $"{language}-Reset-confirm.png");
        page.ConfirmCommand.Execute(null);
        Settle(page);

        Assert.Empty(page.Rows);
        Assert.Equal(Strings.ResetClean, page.Summary);
        Assert.True(page.IsOk);
        Assert.False(page.ShowReset);
        Assert.Null(page.Note);
        Assert.False(File.Exists(AutoexecService.PathIn(steam.GameRoot)));
        Assert.Empty(App.Services.Reset.Pending(steam.Read()));
        window.Close();
    }

    [AvaloniaFact]
    public void The_page_waits_for_the_game_to_close_and_tells_what_stayed()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-high");
        App.Services = Services(folder, steam);
        Assert.Null(App.Services.Launch.Apply(steam.Read(), "-high -novid"));
        steam.Running.Add("dota2");
        (MainWindow window, ResetViewModel page) = Open(steam, "ru");

        Assert.Equal(Strings.ResetDotaRunning, page.Summary);
        Assert.False(page.ShowReset);
        Assert.False(page.ResetCommand.CanExecute(null));

        steam.Running.Remove("dota2");
        steam.Running.Add("steam");
        page.Activated();
        Settle(page);
        page.ResetCommand.Execute(null);
        page.ConfirmCommand.Execute(null);
        Settle(page);

        Assert.Equal([Strings.ResetPartLaunch], page.Rows);
        Assert.Equal(Strings.ResetPartly, page.Summary);
        Assert.True(page.IsAttention);
        Assert.Equal($"{Strings.ResetPartLaunch}: {Strings.ResetProblemSteam}", page.Note);
        Snapshot(window, "ru-Reset-partly.png");
        window.Close();
    }

    private static AppServices Services(CyrillicTempFolder folder, FakeSteam steam) =>
        AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);

    private static void ChangeEverything(AppServices services, FakeSteam steam)
    {
        Assert.Null(services.InstallPack());
        Assert.Null(services.Autoexec.Apply(steam.Read(), [new ConfigLine("dota_friendly_color", "0 255 255")]));
        Assert.Null(services.Transfer.Apply(steam.Read(), FakeSteam.OtherAccountId, FakeSteam.AccountId, [TransferGroup.Hotkeys]).Refusal);
        Assert.Null(services.Launch.Apply(steam.Read(), "-high -novid"));
        HeroGridLayout layout = GridCatalogStore.BuiltIn().Layouts[0];
        Assert.Null(services.HeroGrids.Apply(steam.Read(), FakeSteam.AccountId, [new GridChoice(layout.Name, layout)]).Refusal);
    }

    private static (MainWindow, ResetViewModel) Open(FakeSteam steam, string language)
    {
        UiLanguage.Apply(UiLanguage.Resolve(language));
        MainWindow window = App.CreateMainWindow(SectionKind.Reset);
        window.Show();
        var main = (MainViewModel)window.DataContext!;
        var page = (ResetViewModel)main.Sections.Single(s => s.Kind == SectionKind.Reset).Page;
        Settle(page);
        return (window, page);
    }

    private static void Snapshot(MainWindow window, string name)
    {
        for (int i = 0; i < 4; i++)
        {
            Thread.Sleep(100);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        using Avalonia.Media.Imaging.WriteableBitmap? frame = window.CaptureRenderedFrame();
        string dir = Path.Combine(RepoPaths.Root, "artifacts", "snapshots");
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Settle(ResetViewModel page)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (page.IsBusy && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.False(page.IsBusy, "the reset page did not settle in 10 s");
    }
}
