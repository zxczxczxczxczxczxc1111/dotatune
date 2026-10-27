using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotaTune.Core.HeroGrids;
using DotaTune.Core.Platform;
using DotaTune.Resources;
using DotaTune.ViewModels;
using DotaTune.Views;

namespace DotaTune.Tests;

public class HeroGridPageTests
{
    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Layouts_are_put_in_and_taken_out_from_the_page(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        string file = steam.WriteGameFile(FakeSteam.AccountId, "remote/cfg/hero_grid_config.json", HeroGridTests.GameFile);
        steam.SetCloud(FakeSteam.AccountId, on: true);
        steam.SetGridSeen();
        (MainWindow window, HeroGridViewModel page) = Open(folder, steam, language);

        Assert.Equal(["Stratz - Immortal Meta", "Spectral - League Meta", "Stratz - Turbo Meta"], page.Rows.Select(r => r.Name));
        Assert.All(page.Rows, r => Assert.False(r.IsOn));
        Assert.Equal(Strings.GridNoneInGame, page.Summary);
        Assert.False(page.CanApplyNow);
        Assert.False(page.CanRemoveNow);
        Assert.Contains("222222222", page.AccountText, StringComparison.Ordinal);
        Assert.False(page.CanDownloadEver);

        page.Rows[0].IsOn = true;
        page.Rows[2].IsOn = true;
        Assert.Equal(Strings.GridDiffers, page.Summary);
        Assert.True(page.IsAttention);
        Assert.True(page.CanApplyNow);

        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.GridMatches, page.Summary);
        Assert.True(page.IsOk);
        Assert.False(page.CanApplyNow);
        Assert.True(page.CanRemoveNow);
        Assert.NotNull(page.CloudHint);
        Assert.Equal(
            ["Stratz - Immortal Meta" + GridMark.Suffix, "Stratz - Turbo Meta" + GridMark.Suffix],
            GridFile.Parse(File.ReadAllBytes(file)).Entries.Where(e => e.IsOurs).Select(e => e.Name));
        Snapshot(window, $"{language}-HeroGrid-written.png");

        // The page opened again reads the game: the ticks stay as written.
        page.Activated();
        Settle(page);
        Assert.Equal([true, false, true], page.Rows.Select(r => r.IsOn));

        page.RemoveAllCommand.Execute(null);
        Settle(page);
        Assert.Equal(HeroGridTests.GameFile, File.ReadAllText(file));
        Assert.Equal(Strings.GridNoneInGame, page.Summary);
        Assert.All(page.Rows, r => Assert.False(r.IsOn));
        window.Close();
    }

    [AvaloniaFact]
    public void Layouts_from_a_file_are_ticked_and_written()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        string file = steam.WriteGameFile(FakeSteam.AccountId, "remote/cfg/hero_grid_config.json", HeroGridTests.GameFile);
        string download = Path.Combine(folder.Path, "Загрузки", "d2pt сетка.json");
        Directory.CreateDirectory(Path.GetDirectoryName(download)!);
        File.WriteAllText(download, HeroGridTests.D2ptFile);
        steam.SetGridSeen();
        (MainWindow window, HeroGridViewModel page) = Open(folder, steam, "ru");

        Wait(page.AddFromAsync(download));
        Assert.Equal(5, page.Rows.Count);
        Assert.True(page.Rows.Single(r => r.Name == "Dota2ProTracker 7.41f - Carry").IsOn);
        Assert.Contains("d2pt сетка.json", page.Rows.Single(r => r.Name == "Dota2ProTracker 7.41f - Carry").Description, StringComparison.Ordinal);
        Assert.True(page.CanApplyNow);

        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.GridMatches, page.Summary);
        Assert.Equal(2, GridFile.Parse(File.ReadAllBytes(file)).Entries.Count(e => e.IsOurs));

        Wait(page.AddFromAsync(Path.Combine(folder.Path, "нет такого.json")));
        Assert.StartsWith(Strings.GridFileProblemFormat[..10], page.Note, StringComparison.Ordinal);
        window.Close();

        App.Services.RemoveFromGame();
        Assert.Equal(HeroGridTests.GameFile, File.ReadAllText(file));
        Assert.Empty(App.Services.HeroGrids.Changed());
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Art_grids_are_pictures_written_with_the_rest(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        string file = steam.WriteGameFile(FakeSteam.AccountId, "remote/cfg/hero_grid_config.json", HeroGridTests.GameFile);
        steam.SetGridSeen();
        (MainWindow window, HeroGridViewModel page) = Open(folder, steam, language);
        IReadOnlyList<HeroGridLayout> art = ArtGrids.BuiltIn();

        Assert.Equal(art.Select(l => l.Name), page.ArtRows.Select(r => r.Name));
        Assert.All(page.ArtRows, r => Assert.False(r.IsOn));
        List<GridPreview> previews = [.. window.GetVisualDescendants().OfType<GridPreview>()];
        Assert.Equal(art.Count, previews.Count);
        Assert.All(previews, p => Assert.True(p.Bounds.Height > 100 && p.Bounds.Width > p.Bounds.Height, $"preview is {p.Bounds.Size}"));

        // Every picture at once: quotes, backslashes and hieroglyphs in the names go through the game's format.
        page.Rows[0].IsOn = true;
        foreach (GridRow row in page.ArtRows)
        {
            row.IsOn = true;
        }

        Assert.Equal(Strings.GridDiffers, page.Summary);
        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.GridMatches, page.Summary);
        List<GridEntry> ours = [.. GridFile.Parse(File.ReadAllBytes(file)).Entries.Where(e => e.IsOurs)];
        Assert.Equal(1 + art.Count, ours.Count);
        Assert.All(art, l => Assert.True(ours.Single(e => e.Layout?.Name == l.Name).Layout!.SameAs(l), $"{l.Name} changed on the way"));

        // Read again, the pictures stay in their group and are not listed among the rows.
        page.Activated();
        Settle(page);
        Assert.All(page.ArtRows, r => Assert.True(r.IsOn));
        Assert.DoesNotContain(page.Rows, r => art.Any(l => l.Name == r.Name));
        window.Height = 1900;
        Snapshot(window, $"{language}-HeroGrid-art.png");

        page.RemoveAllCommand.Execute(null);
        Settle(page);
        Assert.Equal(HeroGridTests.GameFile, File.ReadAllText(file));
        Assert.All(page.ArtRows, r => Assert.False(r.IsOn));
        window.Close();
    }

    [AvaloniaFact]
    public void Account_whose_game_saved_no_grid_is_told_how_to_make_one()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.WriteGameFile(FakeSteam.AccountId, "remote/cfg/hero_grid_config.json", HeroGridTests.GameFile);
        (MainWindow window, HeroGridViewModel page) = Open(folder, steam, "ru");

        Assert.Equal(Strings.GridNotSeen, page.Note);
        page.Rows[0].IsOn = true;
        Assert.Equal(Strings.GridDiffers, page.Summary);
        Assert.False(page.CanApplyNow);
        Assert.Equal(Strings.GridNotSeen, page.Note);

        steam.SetGridSeen();
        page.Activated();
        Settle(page);
        Assert.Null(page.Note);
        Assert.True(page.Rows[0].IsOn);
        Assert.True(page.CanApplyNow);
        window.Close();
    }

    [AvaloniaFact]
    public void Running_dota_and_a_broken_file_block_the_buttons()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        string file = steam.WriteGameFile(FakeSteam.AccountId, "remote/cfg/hero_grid_config.json", "{\"configs\": [");
        (MainWindow window, HeroGridViewModel page) = Open(folder, steam, "ru");

        Assert.StartsWith(Strings.GridBrokenFormat[..20], page.Summary, StringComparison.Ordinal);
        Assert.Contains(file, page.Summary, StringComparison.Ordinal);
        page.Rows[0].IsOn = true;
        Assert.False(page.CanApplyNow);

        File.WriteAllText(file, HeroGridTests.GameFile);
        steam.Running.Add("dota2");
        page.Activated();
        Settle(page);
        page.Rows[1].IsOn = true;
        Assert.Equal(Strings.GridDotaRunning, page.Note);
        Assert.False(page.CanApplyNow);
        window.Close();
    }

    private static (MainWindow, HeroGridViewModel) Open(CyrillicTempFolder folder, FakeSteam steam, string language)
    {
        App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);
        UiLanguage.Apply(UiLanguage.Resolve(language));
        MainWindow window = App.CreateMainWindow(SectionKind.HeroGrid);
        window.Show();
        var main = (MainViewModel)window.DataContext!;
        var page = (HeroGridViewModel)main.Sections.Single(s => s.Kind == SectionKind.HeroGrid).Page;
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

    /// <summary>Runs the UI queue until the task ends; blocking on it would stall the UI thread it continues on.</summary>
    private static void Wait(Task task)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.True(task.IsCompletedSuccessfully, "the task did not finish in 10 s");
    }

    private static void Settle(HeroGridViewModel page)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (page.IsBusy && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.False(page.IsBusy, "the hero grid page did not settle in 10 s");
    }
}
