using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotaTune.Core.Platform;
using DotaTune.ViewModels;
using DotaTune.Views;

namespace DotaTune.Tests;

/// <summary>
/// Renders every page of the main window in Russian and in English and saves the
/// pictures to artifacts/snapshots for review. Russian labels are longer, so the
/// test also checks that no text block is wider than the space it got.
/// </summary>
public class WindowSnapshotTests
{
    public static TheoryData<string, SectionKind> Pages()
    {
        var data = new TheoryData<string, SectionKind>();
        foreach (string language in new[] { "ru", "en" })
        {
            foreach (SectionKind kind in Enum.GetValues<SectionKind>())
            {
                data.Add(language, kind);
            }
        }

        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Pages))]
    public void Page_renders_and_fits(string language, SectionKind kind)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.AddPak("russian", 40, withMarker: true);
        steam.SetLaunchOptions("-novid -high");
        App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);
        UiLanguage.Apply(UiLanguage.Resolve(language));

        MainWindow window = App.CreateMainWindow(kind);
        window.Show();
        WaitForGameState((MainViewModel)window.DataContext!);
        if (((MainViewModel)window.DataContext!).Current.Page is EffectsViewModel effects)
        {
            PumpUntil(() => !effects.IsBusy, "effects did not load in 10 s");
        }

        if (((MainViewModel)window.DataContext!).Current.Page is ClicksViewModel clicks)
        {
            PumpUntil(() => !clicks.IsBusy, "clicks page did not load in 10 s");
        }

        if (((MainViewModel)window.DataContext!).Current.Page is ConfigViewModel config)
        {
            PumpUntil(() => !config.IsBusy, "config page did not load in 10 s");
        }

        if (((MainViewModel)window.DataContext!).Current.Page is SettingsViewModel settings)
        {
            PumpUntil(() => !settings.Config.IsBusy, "settings page did not load in 10 s");
        }

        if (((MainViewModel)window.DataContext!).Current.Page is HeroGridViewModel grids)
        {
            PumpUntil(() => !grids.IsBusy, "hero grid page did not load in 10 s");
        }

        if (((MainViewModel)window.DataContext!).Current.Page is MenuBackgroundViewModel menu)
        {
            PumpUntil(() => !menu.IsBusy, "menu background page did not load in 10 s");
        }

        if (((MainViewModel)window.DataContext!).Current.Page is MmrViewModel mmr)
        {
            PumpUntil(() => !mmr.IsBusy, "MMR page did not load in 10 s");
        }

        if (((MainViewModel)window.DataContext!).Current.Page is AutoAcceptViewModel accept)
        {
            PumpUntil(() => !accept.IsBusy, "auto accept page did not load in 10 s");
        }

        if (((MainViewModel)window.DataContext!).Current.Page is ResetViewModel reset)
        {
            PumpUntil(() => !reset.IsBusy, "reset page did not load in 10 s");
        }

        using Avalonia.Media.Imaging.WriteableBitmap? frame = Capture(window);

        Assert.NotNull(frame);
        string dir = Path.Combine(RepoPaths.Root, "artifacts", "snapshots");
        Directory.CreateDirectory(dir);
        frame.Save(Path.Combine(dir, $"{language}-{kind}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

        AssertTextFits(window);

        // A hovered pill grows to 1.04 around its centre: only the scroll view may clip it, and with room to spare.
        foreach (Button pill in window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("pill") && b.IsEffectivelyVisible))
        {
            Visual clip = pill.GetVisualAncestors().First(v => v.ClipToBounds);
            Assert.True(clip is ScrollContentPresenter, $"\"{pill.Content}\" is clipped by {clip.GetType().Name}");
            double grow = pill.Bounds.Width * 0.02;
            double left = pill.TranslatePoint(new Point(-grow, 0), clip)!.Value.X;
            double right = pill.TranslatePoint(new Point(pill.Bounds.Width + grow, 0), clip)!.Value.X;
            Assert.True(left >= 0 && right <= clip.Bounds.Width, $"\"{pill.Content}\" grows past the scroll view");
        }

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Pack_is_installed_and_removed_from_the_page(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);
        UiLanguage.Apply(UiLanguage.Resolve(language));
        MainWindow window = App.CreateMainWindow(SectionKind.GameState);
        window.Show();
        var main = (MainViewModel)window.DataContext!;
        var page = (GameStateViewModel)main.Sections.Single(s => s.Kind == SectionKind.GameState).Page;
        WaitForGameState(main);

        Assert.True(page.InstallPackCommand.CanExecute(null));
        page.InstallPackCommand.Execute(null);
        WaitForGameState(main);

        string pak = Path.Combine(steam.GameRoot, "game", "dota_russian", "pak40_dir.vpk");
        Assert.True(File.Exists(pak));
        Assert.Equal(Resources.Strings.StatePackReady, page.Summary);
        Assert.True(page.RemovePackCommand.CanExecute(null));
        Assert.False(page.Installable);
        using (Avalonia.Media.Imaging.WriteableBitmap? frame = Capture(window))
        {
            frame!.Save(Path.Combine(RepoPaths.Root, "artifacts", "snapshots", $"{language}-GameState-installed.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }

        page.RemovePackCommand.Execute(null);
        WaitForGameState(main);

        Assert.False(File.Exists(pak));
        Assert.Equal(Resources.Strings.StateNoPack, page.Summary);
        Assert.Equal("english", steam.Read().Boot!.AudioLanguage);
        window.Close();
    }

    [AvaloniaFact]
    public void Unreadable_game_archive_is_reported_and_nothing_is_installed()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        File.WriteAllBytes(steam.Pak01, [0x34, 0x12, 0xAA, 0x55, 0, 0, 0, 0]);
        App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);
        UiLanguage.Apply(UiLanguage.Resolve("ru"));
        MainWindow window = App.CreateMainWindow(SectionKind.GameState);
        window.Show();
        var main = (MainViewModel)window.DataContext!;
        var page = (GameStateViewModel)main.Sections.Single(s => s.Kind == SectionKind.GameState).Page;
        WaitForGameState(main);

        page.InstallPackCommand.Execute(null);
        WaitForGameState(main);

        string prefix = Resources.Strings.PackErrorFormat[..Resources.Strings.PackErrorFormat.IndexOf('{', StringComparison.Ordinal)];
        Assert.StartsWith(prefix, page.Note, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(steam.GameRoot, "game", "dota_russian", "pak40_dir.vpk")));
        Assert.Equal(Resources.Strings.StateNoPack, page.Summary);
        window.Close();
    }

    [AvaloniaFact]
    public void Refresh_keeps_the_plate_and_the_buttons_in_place()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        using var held = new ManualResetEventSlim(false);
        int reads = 0;
        App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), () =>
        {
            held.Wait();
            Interlocked.Increment(ref reads);
            return steam.Read();
        });
        UiLanguage.Apply(UiLanguage.Resolve("ru"));
        MainWindow window = App.CreateMainWindow(SectionKind.GameState);
        window.Show();
        var main = (MainViewModel)window.DataContext!;
        var page = (GameStateViewModel)main.Sections.Single(s => s.Kind == SectionKind.GameState).Page;
        Dispatcher.UIThread.RunJobs();

        // Before the first reading lands, the plate itself says the game is being read.
        Border plate = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("verdict"));
        Assert.True(page.IsLoading);
        Assert.True(plate.IsVisible);
        Assert.Equal(Resources.Strings.ReadingGame, page.Summary);
        Assert.False(page.IsOk || page.IsAttention || page.IsError);
        held.Set();
        WaitForGameState(main);

        Button install = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, page.InstallPackCommand));
        Rect plateBefore = plate.Bounds;
        Point? installBefore = install.TranslatePoint(default, window);
        string summary = page.Summary;
        int readsBefore = reads;

        held.Reset();
        page.RefreshCommand.Execute(null);
        page.RefreshCommand.Execute(null);
        page.RefreshCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(page.IsLoading);
        Assert.True(plate.IsVisible);
        Assert.Equal(plateBefore, plate.Bounds);
        Assert.Equal(installBefore, install.TranslatePoint(default, window));
        Assert.Equal(summary, page.Summary);
        Assert.True(install.IsEffectivelyEnabled);
        Assert.True(page.RefreshCommand.CanExecute(null));
        held.Set();
        WaitForGameState(main);

        // The first click reads, the second waits for its turn, the third adds nothing.
        Assert.Equal(readsBefore + 2, reads);

        // A click on install during a reading waits for it, then installs.
        held.Reset();
        page.RefreshCommand.Execute(null);
        page.InstallPackCommand.Execute(null);
        Assert.False(page.InstallPackCommand.CanExecute(null));
        Assert.False(page.RemovePackCommand.CanExecute(null));
        held.Set();
        WaitForGameState(main);

        Assert.True(File.Exists(Path.Combine(steam.GameRoot, "game", "dota_russian", "pak40_dir.vpk")));
        Assert.Equal(Resources.Strings.StatePackReady, page.Summary);
        page.RemovePackCommand.Execute(null);
        WaitForGameState(main);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Play_reports_that_the_game_loaded_the_pack(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        var game = new FakeGame(steam)
        {
            Prints = GameLauncherTests.SearchPathList(steam.GameRoot, @"game\dota_russian\pak40.vpk", @"game\dota_russian\", @"game\dota\"),
        };
        App.Services = AppServices.Create(
            new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read, _ => GameLauncherTests.Launcher(steam, game));
        UiLanguage.Apply(UiLanguage.Resolve(language));
        MainWindow window = App.CreateMainWindow(SectionKind.GameState);
        window.Show();
        var main = (MainViewModel)window.DataContext!;
        var page = (GameStateViewModel)main.Sections.Single(s => s.Kind == SectionKind.GameState).Page;
        WaitForGameState(main);
        page.InstallPackCommand.Execute(null);
        WaitForGameState(main);

        Assert.True(page.PlayIsAccent);
        Assert.True(page.PlayCommand.CanExecute(null));
        page.PlayCommand.Execute(null);
        PumpUntil(() => !page.IsPlaying && !page.IsLoading, "play did not finish in 10 s");

        Assert.Single(game.Starts);
        Assert.Equal(Resources.Strings.PlayPackLoaded, page.Note);
        Assert.True(page.IsOk);
        using (Avalonia.Media.Imaging.WriteableBitmap? frame = Capture(window))
        {
            frame!.Save(Path.Combine(RepoPaths.Root, "artifacts", "snapshots", $"{language}-GameState-played.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }

        page.RemovePackCommand.Execute(null);
        WaitForGameState(main);
        window.Close();
    }

    /// <summary>
    /// Transitions in the headless platform move only on a render tick, so a frame taken
    /// right away shows the start of a hover or class change. Let them finish first.
    /// </summary>
    internal static Avalonia.Media.Imaging.WriteableBitmap? Capture(Window window)
    {
        for (int i = 0; i < 4; i++)
        {
            Thread.Sleep(100);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        return window.CaptureRenderedFrame();
    }

    /// <summary>No text block shown in the window is wider than the space it got.</summary>
    internal static void AssertTextFits(Window window)
    {
        foreach (TextBlock block in window.GetLogicalDescendants().OfType<TextBlock>().Where(b => b.IsEffectivelyVisible))
        {
            Assert.True(
                block.DesiredSize.Width <= block.Bounds.Width + 0.5,
                $"\"{block.Text}\" needs {block.DesiredSize.Width:F0} px but got {block.Bounds.Width:F0}");
        }
    }

    /// <summary>The game state is read on a worker thread; pump the UI queue until it lands.</summary>
    private static void WaitForGameState(MainViewModel main)
    {
        var page = (GameStateViewModel)main.Sections.Single(s => s.Kind == SectionKind.GameState).Page;
        PumpUntil(() => !page.IsLoading, "game state did not load in 10 s");
    }

    private static void PumpUntil(Func<bool> done, string failure)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), failure);
    }
}
