using System.Diagnostics;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Pack;

namespace DotaTune.Core.Game;

public enum PlayOutcome
{
    /// <summary>The game lists the DotaTune pack among its search paths.</summary>
    PackLoaded,

    /// <summary>The game printed its search paths and the pack is not among them.</summary>
    PackNotLoaded,

    /// <summary>No pack is installed, so there was nothing to check after the start.</summary>
    Started,

    /// <summary>dota2 did not appear: Steam waits for sign-in, updates the game, or the start failed.</summary>
    DidNotStart,

    /// <summary>The game closed before it printed its search paths.</summary>
    ClosedEarly,

    /// <summary>The game runs, but console.log has no list of search paths.</summary>
    NoSearchPaths,

    AlreadyRunning,

    NoGame,
}

/// <param name="LanguageFolders">Language folders the game loaded, when it printed its search paths.</param>
public sealed record PlayResult(PlayOutcome Outcome, IReadOnlyList<string> LanguageFolders);

public interface IGameStarter
{
    void Start(string steamExe, IReadOnlyList<string> arguments);
}

/// <summary>Hands the start to Steam. Steam prints nothing worth reading, so its output is not captured.</summary>
public sealed class SteamGameStarter : IGameStarter
{
    public void Start(string steamExe, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        // Steam started again after a restart keeps running: in the DotaTune folder it would hold
        // that folder open and stall an update or uninstall.
        var start = new ProcessStartInfo(steamExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(steamExe) };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process.Start(start)?.Dispose();
    }
}

/// <param name="Start">How long Steam may take to start dota2: it may be signing in or updating the game.</param>
/// <param name="SearchPaths">How long the started game may take to print its search paths.</param>
public sealed record LaunchTimings(TimeSpan Start, TimeSpan SearchPaths, TimeSpan Poll)
{
    public static LaunchTimings Default { get; } = new(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1));
}

/// <summary>
/// The "Play" button: starts Dota through Steam with -condebug and reads console.log to tell
/// whether the game loaded the DotaTune pack.
/// </summary>
public sealed class GameLauncher(IGameStarter starter, IProcessProbe processes, FileLog log, LaunchTimings? timings = null)
{
    private const string DotaProcess = "dota2";
    private readonly LaunchTimings _timings = timings ?? LaunchTimings.Default;

    /// <summary>Only "-" options: Steam runs "+" commands from its own command line itself, "+quit" closes Steam.</summary>
    public static IReadOnlyList<string> Arguments { get; } = ["-applaunch", "570", Steam.LaunchOptionsEditor.ConsoleLog];

    public async Task<PlayResult> PlayAsync(GameState game, InstallState? pack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Steam is null || game.Dota is null)
        {
            return Done(PlayOutcome.NoGame);
        }

        if (game.DotaRunning || processes.IsRunning(DotaProcess))
        {
            return Done(PlayOutcome.AlreadyRunning);
        }

        string console = ConsoleLog.PathIn(game.Dota.GameRoot);
        LogMark mark = ConsoleLog.MarkOf(console);
        starter.Start(Path.Combine(game.Steam.Root, "steam.exe"), Arguments);
        log.Info($"Game started through Steam: {string.Join(' ', Arguments)}");

        var clock = Stopwatch.StartNew();
        while (!processes.IsRunning(DotaProcess))
        {
            if (clock.Elapsed >= _timings.Start)
            {
                return Done(PlayOutcome.DidNotStart);
            }

            await Task.Delay(_timings.Poll, cancellationToken).ConfigureAwait(false);
        }

        if (pack is null)
        {
            return Done(PlayOutcome.Started);
        }

        clock.Restart();
        while (true)
        {
            // Asked before reading, so a game that printed the list and closed is still checked.
            bool running = processes.IsRunning(DotaProcess);
            SearchPaths? paths = TryRead(console, mark);
            if (paths is not null)
            {
                return Done(paths.HasPak(pack.Language, pack.Slot) ? PlayOutcome.PackLoaded : PlayOutcome.PackNotLoaded, paths.LanguageFolders);
            }

            if (!running)
            {
                return Done(PlayOutcome.ClosedEarly);
            }

            if (clock.Elapsed >= _timings.SearchPaths)
            {
                return Done(PlayOutcome.NoSearchPaths);
            }

            await Task.Delay(_timings.Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    private SearchPaths? TryRead(string console, LogMark mark)
    {
        try
        {
            return ConsoleLog.FindSearchPaths(ConsoleLog.ReadAfter(console, mark));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"{console} is not readable yet: {ex.Message}");
            return null;
        }
    }

    private PlayResult Done(PlayOutcome outcome, IReadOnlyList<string>? folders = null)
    {
        folders ??= [];
        log.Info($"Play: {outcome}, language folders [{string.Join(", ", folders)}]");
        return new PlayResult(outcome, folders);
    }
}
