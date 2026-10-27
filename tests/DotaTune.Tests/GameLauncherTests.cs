using System.Text;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Game;
using DotaTune.Core.Pack;

namespace DotaTune.Tests;

public class GameLauncherTests
{
    private static readonly LaunchTimings Fast = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10));

    [Fact]
    public void Search_paths_are_read_as_the_game_prints_them()
    {
        const string root = @"D:\Игры\Steam Library\steamapps\common\dota 2 beta";
        string text = SearchPathList(root, @"game\dota_russian\pak42.vpk", @"game\dota\pak01.vpk", @"game\dota_russian\", @"game\dota\");

        SearchPaths? paths = ConsoleLog.FindSearchPaths(text);

        Assert.NotNull(paths);
        Assert.Equal(4, paths.Game.Count);
        Assert.True(paths.HasPak("russian", 42));
        Assert.False(paths.HasPak("russian", 40));
        Assert.False(paths.HasPak("schinese", 42));
        Assert.Equal(["russian"], paths.LanguageFolders);
    }

    [Fact]
    public void English_voice_loads_no_language_folder()
    {
        SearchPaths? paths = ConsoleLog.FindSearchPaths(SearchPathList(@"D:\games", @"game\dota\pak01.vpk", @"game\dota\", @"game\core\"));

        Assert.NotNull(paths);
        Assert.Empty(paths.LanguageFolders);
    }

    [Fact]
    public void A_list_the_game_is_still_printing_is_not_read()
    {
        string full = SearchPathList(@"D:\games", @"game\dota_russian\pak40.vpk", @"game\dota\pak01.vpk");
        int cut = full.IndexOf("[CommandLine]", StringComparison.Ordinal);

        Assert.Null(ConsoleLog.FindSearchPaths(full[..cut]));
        Assert.Null(ConsoleLog.FindSearchPaths(string.Empty));
        Assert.NotNull(ConsoleLog.FindSearchPaths(full));
    }

    [Fact]
    public void Only_what_was_written_after_the_mark_is_read()
    {
        using var folder = new CyrillicTempFolder();
        string file = Path.Combine(folder.Path, "console.log");
        File.WriteAllText(file, "10/26 10:40:01 старая сессия\n", new UTF8Encoding(false));
        LogMark mark = ConsoleLog.MarkOf(file);

        // The game keeps the file open for writing while it runs.
        using (var game = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            game.Write(Encoding.UTF8.GetBytes("новая сессия\n"));
            game.Flush();
            Assert.Equal("новая сессия\n", ConsoleLog.ReadAfter(file, mark));
        }

        File.WriteAllText(file, "заново\n", new UTF8Encoding(false));
        Assert.Equal("заново\n", ConsoleLog.ReadAfter(file, mark));

        // Started anew and already longer than the old log: still read from the start.
        string longer = "10/26 10:52:17 новая сессия, начало файла другое\n";
        File.WriteAllText(file, longer, new UTF8Encoding(false));
        Assert.Equal(longer, ConsoleLog.ReadAfter(file, mark));

        Assert.Equal(string.Empty, ConsoleLog.ReadAfter(Path.Combine(folder.Path, "нет.log"), LogMark.None));
        Assert.Same(LogMark.None, ConsoleLog.MarkOf(Path.Combine(folder.Path, "нет.log")));
    }

    [Fact]
    public void Steam_gets_no_plus_commands()
    {
        Assert.Equal(["-applaunch", "570", "-condebug"], GameLauncher.Arguments);
        Assert.DoesNotContain(GameLauncher.Arguments, a => a.StartsWith('+'));
    }

    [Fact]
    public async Task Pack_in_the_search_paths_is_reported_as_loaded()
    {
        using var steam = new FakeSteam();
        var game = new FakeGame(steam) { Prints = SearchPathList(steam.GameRoot, @"game\dota_russian\pak40.vpk", @"game\dota_russian\", @"game\dota\") };

        PlayResult result = await Launcher(steam, game).PlayAsync(steam.Read(), Pack(steam), TestContext.Current.CancellationToken);

        Assert.Equal(PlayOutcome.PackLoaded, result.Outcome);
        Assert.Equal(["russian"], result.LanguageFolders);
        string[] start = Assert.Single(game.Starts);
        Assert.Equal(Path.Combine(steam.SteamRoot, "steam.exe"), start[0]);
    }

    [Fact]
    public async Task Pack_line_of_an_earlier_session_does_not_count()
    {
        using var steam = new FakeSteam();
        steam.Write(ConsoleLog.PathIn(steam.GameRoot), SearchPathList(steam.GameRoot, @"game\dota_russian\pak40.vpk", @"game\dota_russian\"));
        var game = new FakeGame(steam) { Prints = SearchPathList(steam.GameRoot, @"game\dota_schinese\", @"game\dota\") };

        PlayResult result = await Launcher(steam, game).PlayAsync(steam.Read(), Pack(steam), TestContext.Current.CancellationToken);

        Assert.Equal(PlayOutcome.PackNotLoaded, result.Outcome);
        Assert.Equal(["schinese"], result.LanguageFolders);
    }

    [Fact]
    public async Task Game_that_never_appears_did_not_start()
    {
        using var steam = new FakeSteam();
        var game = new FakeGame(steam) { Appears = false };

        PlayResult result = await Launcher(steam, game).PlayAsync(steam.Read(), Pack(steam), TestContext.Current.CancellationToken);

        Assert.Equal(PlayOutcome.DidNotStart, result.Outcome);
    }

    [Fact]
    public async Task Game_closed_before_the_list_is_reported()
    {
        using var steam = new FakeSteam();
        var game = new FakeGame(steam) { RunningChecks = 3 };

        PlayResult result = await Launcher(steam, game).PlayAsync(steam.Read(), Pack(steam), TestContext.Current.CancellationToken);

        Assert.Equal(PlayOutcome.ClosedEarly, result.Outcome);
    }

    [Fact]
    public async Task Running_game_without_a_list_times_out()
    {
        using var steam = new FakeSteam();
        var game = new FakeGame(steam) { Prints = "10/26 10:46:48 [RenderSystem] no -condebug here\n" };

        PlayResult result = await Launcher(steam, game).PlayAsync(steam.Read(), Pack(steam), TestContext.Current.CancellationToken);

        Assert.Equal(PlayOutcome.NoSearchPaths, result.Outcome);
    }

    [Fact]
    public async Task Without_a_pack_the_game_is_only_started()
    {
        using var steam = new FakeSteam();
        var game = new FakeGame(steam);

        PlayResult result = await Launcher(steam, game).PlayAsync(steam.Read(), pack: null, TestContext.Current.CancellationToken);

        Assert.Equal(PlayOutcome.Started, result.Outcome);
        Assert.Single(game.Starts);
    }

    [Fact]
    public async Task Running_dota_is_not_started_again()
    {
        using var steam = new FakeSteam();
        steam.Running.Add("dota2");
        var game = new FakeGame(steam);

        PlayResult result = await Launcher(steam, game).PlayAsync(steam.Read(), Pack(steam), TestContext.Current.CancellationToken);

        Assert.Equal(PlayOutcome.AlreadyRunning, result.Outcome);
        Assert.Empty(game.Starts);
    }

    [Fact]
    public async Task Missing_game_is_not_started()
    {
        using var steam = new FakeSteam();
        Directory.Delete(steam.Library, recursive: true);
        var game = new FakeGame(steam);

        PlayResult result = await Launcher(steam, game).PlayAsync(steam.Read(), Pack(steam), TestContext.Current.CancellationToken);

        Assert.Equal(PlayOutcome.NoGame, result.Outcome);
        Assert.Empty(game.Starts);
    }

    internal static InstallState Pack(FakeSteam steam) =>
        new(steam.GameRoot, "russian", 40, "0000", "english", "6967", DateTimeOffset.Now);

    /// <summary>The search path block of console.log, laid out like the one from the probes of 26.10.2026.</summary>
    internal static string SearchPathList(string gameRoot, params string[] game)
    {
        var text = new StringBuilder();
        void Line(string body) => text.Append("10/26 10:46:52 ").Append(body).Append('\n');
        string Full(string relative) => Path.Combine(gameRoot, relative);

        Line("[RenderSystem] Loaded video settings config from 'cfg\\video.txt'");
        Line("[Filesystem] ---------------");
        Line("[Filesystem] Path ID:             File Path:");
        Line($"[Filesystem] ADDONS               \"{Full(@"game\dota_addons\")}\" ");
        foreach (string entry in game)
        {
            string full = Full(entry);
            Line(entry.EndsWith(".vpk", StringComparison.Ordinal)
                ? $"[Filesystem] GAME                 \"{full}\" (vpk) {full}"
                : $"[Filesystem] GAME                 \"{full}\" ");
        }

        Line($"[Filesystem] GAMEROOT             \"{Full(@"game\")}\" ");
        Line("[CommandLine] command line arguments:");
        Line("[CommandLine] -steam -condebug");
        return text.ToString();
    }

    internal static GameLauncher Launcher(FakeSteam steam, FakeGame game) => new(game, game, new FileLog(Path.Combine(steam.DataRoot, "logs")), Fast);
}

/// <summary>Stands in for Steam and dota2: records the start and prints into console.log.</summary>
internal sealed class FakeGame(FakeSteam steam) : IGameStarter, IProcessProbe
{
    private bool _started;

    public List<string[]> Starts { get; } = [];

    /// <summary>Whether dota2 shows up after the start.</summary>
    public bool Appears { get; init; } = true;

    /// <summary>What the game appends to console.log when it starts.</summary>
    public string? Prints { get; init; }

    /// <summary>How many times dota2 is reported running before it closes.</summary>
    public int RunningChecks { get; set; } = int.MaxValue;

    public void Start(string steamExe, IReadOnlyList<string> arguments)
    {
        Starts.Add([steamExe, .. arguments]);
        if (!Appears)
        {
            return;
        }

        _started = true;
        if (Prints is not null)
        {
            string file = ConsoleLog.PathIn(steam.GameRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, Prints, new UTF8Encoding(false));
        }
    }

    public bool IsRunning(string processName)
    {
        if (string.Equals(processName, "dota2", StringComparison.Ordinal) && _started)
        {
            return RunningChecks-- > 0;
        }

        return steam.IsRunning(processName);
    }
}
