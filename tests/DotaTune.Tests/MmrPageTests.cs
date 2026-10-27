using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DotaTune.Core.Effects;
using DotaTune.Core.Game;
using DotaTune.Core.Mmr;
using DotaTune.Core.Platform;
using DotaTune.Resources;
using DotaTune.ViewModels;
using DotaTune.Views;
using ValvePak;

namespace DotaTune.Tests;

public class MmrPageTests
{
    private const long At = 1_793_070_000;

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Line_goes_into_the_pack_and_readings_reach_the_page_and_the_pack(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        AddGameLayout(steam);
        (MainWindow window, MmrViewModel page) = Open(folder, steam, language);

        Assert.False(page.IsOn);
        Assert.Equal(Strings.StateNoPack, page.Summary);
        Assert.False(page.CanApplyNow);
        Assert.Equal(Strings.MmrHistoryEmpty, page.HistoryNote);
        Assert.True(page.NeedsConsoleLog);
        Assert.False(page.ShowConsoleLogHint);

        page.IsOn = true;
        Assert.True(App.Services.Settings.Load().MmrLine);
        Assert.Equal(Strings.MmrNoPack, page.Summary);
        Assert.True(page.ShowConsoleLogHint);
        Assert.Equal(Strings.InstallPack, page.ApplyText);
        Assert.True(page.ApplyCommand.CanExecute(null));

        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.MmrPackWithLine, page.Summary);
        Assert.True(page.IsOk);
        Assert.False(page.CanApplyNow);
        Assert.True(App.Services.Packs.Current!.Mmr);
        Assert.NotNull(ReadFromPack(MmrMenu.BasePath));
        Assert.Contains($"\"lang\":\"{language}\"", DataText(), StringComparison.Ordinal);

        // A session played: the readings come from console.log, and the pack counts from the newest.
        File.WriteAllText(ConsoleLog.PathIn(steam.GameRoot), string.Concat(
            MmrTests.Reading(FakeSteam.AccountId, 3450, null, null, "a", At),
            MmrTests.Reading(FakeSteam.AccountId, 3400, -50, 2, "b", At + 3600)));
        page.Activated();
        Settle(page);
        Assert.Null(page.HistoryNote);
        Assert.Equal(["3400", "3450"], page.Rows.Select(r => r.Mmr));
        Assert.Equal(language == "ru" ? "-50 за 2 игры" : "-50 over 2 games", page.Rows[0].Change);
        Assert.True(page.Rows[0].IsDown);
        Assert.Null(page.Rows[1].Change);
        Assert.All(page.Rows, r => Assert.Null(r.Account));
        Assert.Contains("\"mmr\":3400", DataText(), StringComparison.Ordinal);
        Assert.Equal(Strings.MmrPackWithLine, page.Summary);
        using (Avalonia.Media.Imaging.WriteableBitmap? frame = WindowSnapshotTests.Capture(window))
        {
            frame!.Save(Path.Combine(RepoPaths.Root, "artifacts", "snapshots", $"{language}-Mmr-history.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }

        // Off again: the pack is built without the line and keeps the game's own menu.
        page.IsOn = false;
        Assert.Equal(Strings.MmrPackBuiltWithLine, page.Summary);
        Assert.True(page.IsAttention);
        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.MmrPackWithoutLine, page.Summary);
        Assert.False(App.Services.Packs.Current!.Mmr);
        Assert.Null(ReadFromPack(MmrMenu.BasePath));

        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaFact]
    public void Game_with_another_menu_layout_gets_no_line()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, MmrViewModel page) = Open(folder, steam, "ru");

        page.IsOn = true;
        Assert.Equal(Strings.MmrNotSupported, page.Summary);
        Assert.True(page.IsAttention);
        Assert.False(page.CanApplyNow);

        // A pack put in from another page still leaves the menu alone.
        App.Services.InstallPack();
        page.Activated();
        Settle(page);
        Assert.False(App.Services.Packs.Current!.Mmr);
        Assert.Null(ReadFromPack(MmrMenu.BasePath));
        Assert.Equal(Strings.MmrNotSupported, page.Summary);
        Assert.False(page.CanApplyNow);

        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaFact]
    public void Readings_of_several_accounts_carry_the_account_and_a_broken_history_is_told()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-novid -condebug");
        App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);
        App.Services.Mmr.History.Add([
            new MmrReading(FakeSteam.AccountId, 3450, 25, 1, null, At),
            new MmrReading(FakeSteam.OtherAccountId, 5000, null, null, null, At + 10)]);
        (MainWindow window, MmrViewModel page) = Open(folder, steam, "ru", keepServices: true);

        Assert.False(page.NeedsConsoleLog);
        Assert.Equal(["другой (111111111)", "demo_player (222222222)"], page.Rows.Select(r => r.Account));
        Assert.Equal("+25", page.Rows[1].Change);
        Assert.True(page.Rows[1].IsUp);

        File.WriteAllText(App.Services.Paths.MmrHistoryFile, "[{");
        page.Activated();
        Settle(page);
        Assert.Equal(Strings.MmrHistoryUnreadable, page.HistoryNote);
        Assert.Empty(page.Rows);
        Assert.Equal("[{", File.ReadAllText(App.Services.Paths.MmrHistoryFile));
        window.Close();
    }

    /// <summary>Puts the root menu layout of build 6967 into the game archive.</summary>
    internal static void AddGameLayout(FakeSteam steam) =>
        GameFiles.WritePak(steam.Pak01, new Dictionary<string, byte[]>(GameFiles.Default()) { [MmrMenu.BasePath] = MmrTests.ValveBase() });

    private static (MainWindow, MmrViewModel) Open(CyrillicTempFolder folder, FakeSteam steam, string language, bool keepServices = false)
    {
        if (!keepServices)
        {
            App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);
        }

        UiLanguage.Apply(UiLanguage.Resolve(language));
        MainWindow window = App.CreateMainWindow(SectionKind.Mmr);
        window.Show();
        var main = (MainViewModel)window.DataContext!;
        var page = (MmrViewModel)main.Sections.Single(s => s.Kind == SectionKind.Mmr).Page;
        Settle(page);
        return (window, page);
    }

    private static string DataText() => Encoding.UTF8.GetString(CompiledResource.Data(ReadFromPack(MmrMenu.DataPath)));

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

    private static void Settle(MmrViewModel page)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (page.IsBusy && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.False(page.IsBusy, "the MMR page did not settle in 10 s");
    }
}
