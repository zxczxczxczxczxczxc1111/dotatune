using System.Globalization;
using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Effects;
using DotaTune.Core.Pack;
using DotaTune.Core.Platform;
using DotaTune.Core.Settings;
using DotaTune.Resources;
using DotaTune.Updates;
using DotaTune.ViewModels;
using DotaTune.Views;

namespace DotaTune.Tests;

public class UpdateTests
{
    private const string Newer = "9.0.0";

    [AvaloniaFact]
    public void Without_an_updater_nothing_is_checked()
    {
        using var folder = new CyrillicTempFolder();
        var service = new UpdateService(null, Log(folder));

        Assert.True(service.StartAsync().IsCompleted);
        Assert.Equal(UpdateStage.NotInstalled, service.Stage);
        Assert.Equal(Strings.UpdateNotInstalled, service.Status);
        Assert.False(service.IsPanelOpen);
        Assert.False(service.CanCheck);
    }

    [AvaloniaFact]
    public void Build_without_installation_does_not_ask_for_updates()
    {
        using var folder = new CyrillicTempFolder();
        var updater = new FakeUpdater { IsInstalled = false, Newer = Newer };
        UpdateService service = Service(folder, updater);

        Start(service);
        Assert.Equal(UpdateStage.NotInstalled, service.Stage);
        Assert.Equal(0, updater.Checks);
        Assert.False(service.IsPanelOpen);
    }

    [AvaloniaFact]
    public void Latest_version_shows_only_in_the_settings()
    {
        using var folder = new CyrillicTempFolder();
        UpdateService service = Service(folder, new FakeUpdater());

        Start(service);
        Assert.Equal(UpdateStage.Latest, service.Stage);
        Assert.Equal(Strings.UpdateLatest, service.Status);
        Assert.False(service.IsPanelOpen);
        Assert.True(service.CheckNowCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Version_found_at_start_downloads_under_the_panel_and_installs_once_the_work_is_done()
    {
        using var folder = new CyrillicTempFolder();
        var updater = new FakeUpdater { Newer = Newer, DownloadGate = new TaskCompletionSource() };
        UpdateService service = Service(folder, updater);
        int busy = 3, asked = 0;
        bool closed = false;
        service.IsWorking = () =>
        {
            asked++;
            return busy-- > 0;
        };
        service.Shutdown = () => closed = true;

        Start(service, UpdateStage.Downloading);
        Pump(() => service.Percent == 50);
        Assert.True(service.IsPanelOpen);
        Assert.Equal(Format(Strings.UpdateTitleFormat, Newer), service.PanelTitle);
        Assert.Equal(Format(Strings.UpdateDownloadingFormat, UpdateService.CurrentVersion, 50), service.PanelLead);
        Assert.Equal(Strings.UpdateKeeps, service.PanelNote);
        Assert.True(service.ShowLater);
        Assert.False(service.IsProgressIndeterminate);

        updater.DownloadGate.SetResult();
        Pump(() => service.Stage == UpdateStage.Installing);
        Assert.Equal(4, asked);
        Assert.Equal([(Newer, true)], updater.Installs);
        Assert.True(closed);
        Assert.Equal(Strings.UpdateInstalling, service.PanelLead);
        Assert.False(service.ShowLater);

        // The updater waits for the exit already: closing must not start it a second time.
        service.InstallOnExit();
        Assert.Single(updater.Installs);
    }

    [AvaloniaFact]
    public void Later_puts_the_install_off_until_the_exit()
    {
        using var folder = new CyrillicTempFolder();
        var updater = new FakeUpdater { Newer = Newer, DownloadGate = new TaskCompletionSource() };
        UpdateService service = Service(folder, updater);
        bool closed = false;
        service.Shutdown = () => closed = true;

        Start(service, UpdateStage.Downloading);
        service.LaterCommand.Execute(null);
        Assert.False(service.IsPanelOpen);

        updater.DownloadGate.SetResult();
        Pump(() => service.Stage == UpdateStage.Downloaded);
        Assert.False(service.IsPanelOpen);
        Assert.Empty(updater.Installs);
        Assert.False(closed);
        Assert.Equal(Format(Strings.UpdateDownloadedFormat, Newer), service.Status);
        Assert.True(service.InstallNowCommand.CanExecute(null));

        service.InstallOnExit();
        Assert.Equal([(Newer, false)], updater.Installs);
    }

    [AvaloniaFact]
    public void Later_ends_a_wait_for_the_work()
    {
        using var folder = new CyrillicTempFolder();
        var updater = new FakeUpdater { Newer = Newer };
        var service = new UpdateService(() => updater, Log(folder), delay: _ => Task.Delay(10));
        bool closed = false;
        service.IsWorking = () => true;
        service.Shutdown = () => closed = true;

        Start(service, UpdateStage.Waiting);
        Assert.True(service.ShowLater);
        Assert.Equal(Strings.UpdateWaiting, service.PanelLead);

        service.LaterCommand.Execute(null);
        Pump(() => service.Stage == UpdateStage.Downloaded);
        Assert.False(service.IsPanelOpen);
        Assert.Empty(updater.Installs);
        Assert.False(closed);

        service.InstallOnExit();
        Assert.Equal([(Newer, false)], updater.Installs);
    }

    [AvaloniaFact]
    public void Version_found_late_waits_for_the_exit_or_for_install_now()
    {
        using var folder = new CyrillicTempFolder();
        DateTimeOffset now = DateTimeOffset.Now;
        var updater = new FakeUpdater { Newer = Newer };
        UpdateService service = Service(folder, updater, () => now);
        updater.OnCheck = () => now += UpdateService.PanelWindow + TimeSpan.FromSeconds(1);
        bool closed = false;
        service.Shutdown = () => closed = true;

        Start(service, UpdateStage.Downloaded);
        Assert.False(service.IsPanelOpen);
        Assert.Empty(updater.Installs);

        service.InstallNowCommand.Execute(null);
        Pump(() => service.Stage == UpdateStage.Installing);
        Assert.True(service.IsPanelOpen);
        Assert.Equal([(Newer, true)], updater.Installs);
        Assert.True(closed);
    }

    [AvaloniaTheory]
    [InlineData(403, UpdateStage.Limited)]
    [InlineData(429, UpdateStage.Limited)]
    [InlineData(0, UpdateStage.NoConnection)]
    [InlineData(404, UpdateStage.CheckFailed)]
    [InlineData(-1, UpdateStage.CheckFailed)]
    public void Failed_check_shows_only_in_the_settings(int status, UpdateStage expected)
    {
        using var folder = new CyrillicTempFolder();
        Exception error = status switch
        {
            -1 => new InvalidDataException("releases.win.json is broken"),
            0 => new HttpRequestException("No such host is known"),
            _ => new HttpRequestException("refused", null, (HttpStatusCode)status),
        };
        UpdateService service = Service(folder, new FakeUpdater { CheckError = error });

        Start(service, expected);
        Assert.False(service.IsPanelOpen);
        Assert.True(service.CheckNowCommand.CanExecute(null));
        Assert.Equal(
            expected switch
            {
                UpdateStage.Limited => Strings.UpdateLimited,
                UpdateStage.NoConnection => Strings.UpdateNoConnection,
                _ => Strings.UpdateCheckFailed,
            },
            service.Status);
    }

    [Fact]
    public void Failure_is_found_inside_another_exception()
    {
        var inner = new HttpRequestException("rate limit", null, HttpStatusCode.Forbidden);
        Assert.Equal(UpdateStage.Limited, UpdateService.CheckFailure(new InvalidOperationException("feed", inner)));
        Assert.Equal(UpdateStage.NoConnection, UpdateService.CheckFailure(new TaskCanceledException("timeout")));
    }

    [AvaloniaFact]
    public void Download_that_loses_the_connection_keeps_this_version()
    {
        using var folder = new CyrillicTempFolder();
        var updater = new FakeUpdater { Newer = Newer, DownloadError = new HttpRequestException("The connection was reset") };
        UpdateService service = Service(folder, updater);

        Start(service, UpdateStage.Failed);
        Assert.True(service.IsPanelOpen);
        Assert.True(service.IsFailed);
        Assert.Equal(Strings.UpdateFailedTitle, service.PanelTitle);
        Assert.Equal(Format(Strings.UpdateConnectionLostFormat, UpdateService.CurrentVersion), service.PanelLead);
        Assert.Null(service.PanelNote);
        Assert.Equal(Format(Strings.UpdateFailedStatusFormat, Newer), service.Status);

        service.ClosePanelCommand.Execute(null);
        Assert.False(service.IsPanelOpen);
        service.InstallOnExit();
        Assert.Empty(updater.Installs);
        Assert.True(service.CheckNowCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Updater_that_does_not_start_leaves_the_program_open()
    {
        using var folder = new CyrillicTempFolder();
        var updater = new FakeUpdater { Newer = Newer, InstallError = new IOException("Update.exe is missing") };
        UpdateService service = Service(folder, updater);
        bool closed = false;
        service.Shutdown = () => closed = true;

        Start(service, UpdateStage.Failed);
        Assert.False(closed);
        Assert.Equal(Format(Strings.UpdateFailedFormat, UpdateService.CurrentVersion), service.PanelLead);
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Update_panel_covers_the_pages_and_outlives_a_language_switch(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        var updater = new FakeUpdater { Newer = Newer, DownloadGate = new TaskCompletionSource() };
        App.Services = AppServices.Create(Paths(folder), steam.Read, updater: _ => updater);
        UiLanguage.Apply(UiLanguage.Resolve(language));
        MainWindow window = App.CreateMainWindow(SectionKind.GameState);
        window.Show();
        var main = (MainViewModel)window.DataContext!;

        Start(main.Updates, UpdateStage.Downloading);
        Pump(() => main.Updates.Percent == 50);
        Assert.True(Shown(window, main.Updates.PanelTitle));
        Assert.All(Pages(window), b => Assert.False(b.IsEffectivelyEnabled));
        Snapshot(window, $"{language}-Update-downloading.png");

        MainWindow fresh = App.CreateMainWindow(SectionKind.Settings);
        fresh.Show();
        window.Close();
        var again = (MainViewModel)fresh.DataContext!;
        Assert.Same(main.Updates, again.Updates);
        Assert.True(Shown(fresh, again.Updates.PanelTitle));

        again.Updates.LaterCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Shown(fresh, again.Updates.PanelTitle));
        Assert.All(Pages(fresh), b => Assert.True(b.IsEffectivelyEnabled));
        Assert.True(Shown(fresh, again.Updates.Status));
        // Let the download end here: left behind, it would log into this folder after it is deleted.
        updater.DownloadGate.SetResult();
        Pump(() => again.Updates.Stage == UpdateStage.Downloaded);
        fresh.Close();
    }

    [AvaloniaFact]
    public void Fresh_install_records_the_version_and_writes_the_effect_groups_out()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        App.Services = AppServices.Create(Paths(folder), steam.Read);

        App.Services.News.Detect();
        Assert.False(App.Services.News.IsOpen);
        AppSettings settings = App.Services.Settings.Load();
        Assert.Equal(AppServices.AppVersion, settings.LastVersion);
        Assert.Equal(EffectRules.All, settings.EffectGroups);
    }

    [AvaloniaFact]
    public void First_start_after_a_version_without_the_number_shows_what_is_new_and_offers_the_pack()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        App.Services = AppServices.Create(Paths(folder), steam.Read);
        Assert.Null(App.Services.InstallPack());
        App.Services.Settings.Save(new AppSettings { Language = "en" });

        UpdateNews news = App.Services.News;
        news.Detect();
        Assert.True(news.IsOpen);
        Assert.True(news.PackIsOld);
        Assert.True(news.ShowRebuild);
        Assert.Equal(Strings.NewsPackOld, news.PackNote);
        Assert.Equal(ReleaseNotes.For(AppServices.AppVersion), news.Notes);
        AppSettings settings = App.Services.Settings.Load();
        Assert.Equal("en", settings.Language);
        Assert.Equal(AppServices.AppVersion, settings.LastVersion);
        Assert.Equal(EffectRules.All, settings.EffectGroups);

        // The card shows once.
        news.IsOpen = false;
        news.Detect();
        Assert.False(news.IsOpen);
    }

    [AvaloniaTheory]
    [InlineData("0.1.0", true)]
    [InlineData("99.0.0", false)]
    public void Card_shows_only_after_an_older_version(string last, bool shown)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        App.Services = AppServices.Create(Paths(folder), steam.Read);
        App.Services.Settings.Save(new AppSettings { LastVersion = last, EffectGroups = [EffectGroup.Weather] });

        App.Services.News.Detect();
        // No pack: the card is only for the notes, and the version has them.
        Assert.Equal(shown && App.Services.News.HasNotes, App.Services.News.IsOpen);
        Assert.False(App.Services.News.PackIsOld);
        Assert.True(App.Services.News.ShowClose);
        AppSettings settings = App.Services.Settings.Load();
        // A newer number stays: a build from the sources must not bring the card back to the installed one.
        Assert.Equal(shown ? AppServices.AppVersion : last, settings.LastVersion);
        Assert.Equal([EffectGroup.Weather], settings.EffectGroups);
    }

    [AvaloniaFact]
    public void Version_found_while_the_news_card_is_up_waits_for_the_exit()
    {
        using var folder = new CyrillicTempFolder();
        var updater = new FakeUpdater { Newer = Newer };
        UpdateService service = Service(folder, updater);
        service.KeepsPanelClosed = () => true;

        Start(service, UpdateStage.Downloaded);
        Assert.False(service.IsPanelOpen);
        Assert.Empty(updater.Installs);
    }

    [AvaloniaFact]
    public void Download_held_by_another_copy_stays_quiet()
    {
        using var folder = new CyrillicTempFolder();
        // Velopack makes this one only itself.
        var locked = (Exception)Activator.CreateInstance(typeof(Velopack.Exceptions.AcquireLockFailedException), nonPublic: true)!;
        var updater = new FakeUpdater { Newer = Newer, DownloadError = locked };
        UpdateService service = Service(folder, updater);

        Start(service, UpdateStage.Elsewhere);
        Assert.False(service.IsPanelOpen);
        Assert.Equal(Format(Strings.UpdateElsewhereFormat, Newer), service.Status);
        // A check would only open the panel and meet the lock again.
        Assert.False(service.CheckNowCommand.CanExecute(null));
        service.InstallOnExit();
        Assert.Empty(updater.Installs);
    }

    [AvaloniaFact]
    public void Preset_with_every_group_says_every_group()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        App.Services = AppServices.Create(Paths(folder), steam.Read);
        App.Services.Settings.Save(new AppSettings { EffectGroups = EffectRules.All });
        MainWindow window = App.CreateMainWindow(SectionKind.Config);
        window.Show();
        var config = (ConfigViewModel)((MainViewModel)window.DataContext!).Current.Page;
        Pump(() => !config.IsBusy);

        config.Presets.CopyCodeCommand.Execute(null);
        Pump(() => config.Presets.Code is not null);
        Assert.Null(Core.Presets.PresetCode.Decode(config.Presets.Code!).Effects);

        App.Services.Settings.Save(new AppSettings { EffectGroups = [EffectGroup.Weather] });
        config.Presets.CopyCodeCommand.Execute(null);
        Pump(() => Core.Presets.PresetCode.Decode(config.Presets.Code!).Effects is { Count: 1 });
        window.Close();
    }

    [AvaloniaFact]
    public void Unreadable_settings_are_left_as_they_are()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        App.Services = AppServices.Create(Paths(folder), steam.Read);
        Directory.CreateDirectory(Path.GetDirectoryName(App.Services.Paths.SettingsFile)!);
        File.WriteAllText(App.Services.Paths.SettingsFile, "{ \"language\": ");

        App.Services.News.Detect();
        Assert.False(App.Services.News.IsOpen);
        Assert.Equal("{ \"language\": ", File.ReadAllText(App.Services.Paths.SettingsFile));
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Card_rebuilds_the_pack_and_says_so(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        App.Services = AppServices.Create(Paths(folder), steam.Read);
        Assert.Null(App.Services.InstallPack());
        App.Services.Settings.Save(new AppSettings { Language = language });
        UiLanguage.Apply(UiLanguage.Resolve(language));
        App.Services.News.Detect();
        MainWindow window = App.CreateMainWindow(SectionKind.GameState);
        window.Show();
        var main = (MainViewModel)window.DataContext!;
        UpdateNews news = main.News;
        Assert.True(Shown(window, news.Title));
        Assert.All(Pages(window), b => Assert.False(b.IsEffectivelyEnabled));
        Snapshot(window, $"{language}-Update-news.png");

        // Dota holds the pack open: the card says why and offers the rebuild again.
        steam.Running.Add("dota2");
        news.RebuildCommand.Execute(null);
        Pump(() => !news.IsRebuilding);
        Assert.Equal(Strings.DotaRunningNote, news.PackNote);
        Assert.True(news.ShowRebuild);

        steam.Running.Clear();
        string before = App.Services.Packs.Current!.Sha1;
        news.RebuildCommand.Execute(null);
        Pump(() => !news.IsRebuilding);
        Assert.Equal(Strings.NewsRebuilt, news.PackNote);
        Assert.False(news.PackIsOld);
        Assert.True(news.ShowClose);
        Assert.NotEqual(before, App.Services.Packs.Current!.Sha1);

        main.CloseNewsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(news.IsOpen);
        Assert.All(Pages(window), b => Assert.True(b.IsEffectivelyEnabled));
        window.Close();
    }

    [Fact]
    public void Rebuild_takes_what_the_installed_pack_holds_and_nothing_new()
    {
        var mouse = new MouseColorChoice();
        var pack = new InstallState(
            "D:\\Steam\\steamapps\\common\\dota 2 beta", "russian", 40, "AB", null, "6969", DateTimeOffset.Now,
            new Dictionary<EffectGroup, int> { [EffectGroup.Sprays] = 0, [EffectGroup.Weather] = 12 },
            "black", mouse, null, Mmr: true, AutoAccept: 5, HideEvents: false);
        var settings = new AppSettings
        {
            Language = "en",
            EffectGroups = EffectRules.All,
            MenuBackground = "sample:glance",
            MmrLine = false,
            HideHomeEvents = true,
            AutoAccept = false,
            AutoAcceptDelay = 9,
            LastBackup = "copy.zip",
        };

        AppSettings choice = AppServices.InstalledChoice(pack, settings);
        Assert.Equal([EffectGroup.Weather, EffectGroup.Sprays], choice.EffectGroups);
        Assert.Equal("black", choice.MenuBackground);
        Assert.Same(mouse, choice.MouseColors);
        Assert.True(choice.MmrLine);
        Assert.False(choice.HideHomeEvents);
        Assert.True(choice.AutoAccept);
        Assert.Equal(5, choice.AutoAcceptDelay);
        Assert.Equal("en", choice.Language);
        Assert.Equal("copy.zip", choice.LastBackup);

        AppSettings bare = AppServices.InstalledChoice(pack with { Effects = null, AutoAccept = null }, settings);
        Assert.Empty(bare.EffectGroups!);
        Assert.False(bare.AutoAccept);
        Assert.Equal(9, bare.AutoAcceptDelay);
    }

    [AvaloniaFact]
    public void Rebuild_without_a_pack_does_nothing()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        App.Services = AppServices.Create(Paths(folder), steam.Read);
        Assert.Equal(InstallRefusal.NothingToDo, App.Services.RebuildInstalledPack());
        Assert.Null(App.Services.Packs.Current);
    }

    [Fact]
    public void Release_notes_are_the_section_of_the_version()
    {
        string changelog = string.Join(
            "\r\n",
            "# Изменения",
            "",
            "## [0.4.0] - 2027-01-02",
            "",
            "### Добавлено",
            "",
            "- «Обновления»: программа сама ставит новую версию, `autoexec.cfg` на",
            "  месте, подробности в [README](https://example.com/readme).",
            "- **Пересборка** пака одной кнопкой.",
            "",
            "Строка без пункта.",
            "",
            "## [0.3.0] - 2027-01-01",
            "",
            "- Старое.");

        IReadOnlyList<NoteLine> notes = ReleaseNotes.Parse(changelog, "0.4.0");
        Assert.Equal(
            [
                new NoteLine(NoteKind.Heading, "Добавлено"),
                new NoteLine(NoteKind.Item, "«Обновления»: программа сама ставит новую версию, autoexec.cfg на месте, подробности в README."),
                new NoteLine(NoteKind.Item, "Пересборка пака одной кнопкой."),
                new NoteLine(NoteKind.Text, "Строка без пункта."),
            ],
            notes);
        Assert.Equal([new NoteLine(NoteKind.Item, "Старое.")], ReleaseNotes.Parse(changelog, "0.3.0"));
        Assert.Empty(ReleaseNotes.Parse(changelog, "0.3"));
        Assert.Empty(ReleaseNotes.Parse(changelog, "0.5.0"));
    }

    [Fact]
    public void Release_notes_come_from_the_built_in_changelog()
    {
        IReadOnlyList<NoteLine> first = ReleaseNotes.For("0.1.0");
        Assert.Equal(new NoteLine(NoteKind.Text, "Первый выпуск."), first[0]);
        Assert.Contains(first, n => n.IsHeading);
        Assert.Empty(ReleaseNotes.For("0.0.1"));
    }

    [Theory]
    [InlineData("0.2.0", "0.3.0", true)]
    [InlineData("0.3.0", "0.3.0", false)]
    [InlineData("0.10.0", "0.9.0", false)]
    [InlineData("0.2.9", "0.10.0", true)]
    [InlineData("0.3.0-beta", "0.3.0", false)]
    [InlineData("old", "0.3.0", false)]
    public void Older_version_is_compared_by_numbers(string last, string current, bool older) =>
        Assert.Equal(older, UpdateNews.IsOlder(last, current));

    private static AppPaths Paths(CyrillicTempFolder folder) => new(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work"));

    private static FileLog Log(CyrillicTempFolder folder) => new(Path.Combine(folder.Path, "logs"));

    private static UpdateService Service(CyrillicTempFolder folder, FakeUpdater updater, Func<DateTimeOffset>? clock = null) =>
        new(() => updater, Log(folder), clock, _ => Task.CompletedTask);

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    private static void Start(UpdateService service, UpdateStage? until = null)
    {
        _ = service.StartAsync();
        Pump(() => until is { } stage ? service.Stage == stage : service.Stage != UpdateStage.Checking);
    }

    /// <summary>What a card covers: the side list, the settings entry and the page on the right.</summary>
    private static List<Control> Pages(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        List<Control> pages =
        [
            .. window.GetVisualDescendants().OfType<Control>()
                .Where(c => c is ListBoxItem || (c is Button b && b.Classes.Contains("nav")) || (c is ContentControl p && p.Classes.Contains("page"))),
        ];
        Assert.True(pages.Count > 3, $"only {pages.Count} controls of the pages found");
        return pages;
    }

    private static bool Shown(Window window, string text)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetLogicalDescendants().OfType<TextBlock>().Any(b => b.IsEffectivelyVisible && string.Equals(b.Text, text, StringComparison.Ordinal));
    }

    private static void Snapshot(Window window, string name)
    {
        using Avalonia.Media.Imaging.WriteableBitmap? frame = WindowSnapshotTests.Capture(window);
        Assert.NotNull(frame);
        string dir = Path.Combine(RepoPaths.Root, "artifacts", "snapshots");
        Directory.CreateDirectory(dir);
        frame.Save(Path.Combine(dir, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        WindowSnapshotTests.AssertTextFits(window);
    }

    private static void Pump(Func<bool> done)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), "the update did not settle in 10 s");
    }

    private sealed class FakeUpdater : IAppUpdater
    {
        private int _checks;

        public bool IsInstalled { get; init; } = true;

        public string? Newer { get; init; }

        public Exception? CheckError { get; init; }

        public Exception? DownloadError { get; init; }

        public Exception? InstallError { get; init; }

        /// <summary>Holds the download half way until the test lets it go.</summary>
        public TaskCompletionSource? DownloadGate { get; init; }

        public Action? OnCheck { get; set; }

        public int Checks => _checks;

        public List<(string Version, bool Restart)> Installs { get; } = [];

        public Task<AppUpdate?> CheckAsync()
        {
            Interlocked.Increment(ref _checks);
            OnCheck?.Invoke();
            return CheckError is { } error
                ? Task.FromException<AppUpdate?>(error)
                : Task.FromResult(Newer is null ? null : new AppUpdate(Newer));
        }

        public async Task DownloadAsync(AppUpdate update, Action<int> progress)
        {
            progress(50);
            if (DownloadGate is { } gate)
            {
                await gate.Task;
            }

            if (DownloadError is { } error)
            {
                throw error;
            }

            progress(100);
        }

        public void InstallAfterExit(AppUpdate update, bool restart)
        {
            if (InstallError is { } error)
            {
                throw error;
            }

            Installs.Add((update.Version, restart));
        }
    }
}
