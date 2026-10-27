using System.ComponentModel;
using System.Diagnostics;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Steam;

namespace DotaTune.Core.Game;

public enum SteamRestartProblem
{
    NoSteam,

    /// <summary>Closing Steam would end the game.</summary>
    DotaRunning,

    /// <summary>Steam was still running when the wait ran out; nothing was changed.</summary>
    DidNotClose,

    /// <summary>The change was made, but steam.exe could not be started again.</summary>
    DidNotStart,
}

/// <param name="Result">What the change returned; default when it did not run.</param>
public sealed record SteamRestartResult<T>(SteamRestartProblem? Problem, T Result);

/// <param name="Close">How long Steam may take to close: it may finish a cloud sync first.</param>
/// <param name="Helpers">How long to wait for its web helpers after steam.exe is gone; they do not stop the change.</param>
public sealed record SteamRestartTimings(TimeSpan Close, TimeSpan Poll, TimeSpan Helpers)
{
    public static SteamRestartTimings Default { get; } = new(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10));
}

/// <summary>
/// Closes Steam for a change it would undo, makes the change and starts Steam again. Steam keeps
/// localconfig.vdf in memory and writes it back when it closes, so the launch options can only be
/// changed while it is closed.
/// </summary>
public sealed class SteamRestart(IGameStarter starter, IProcessProbe processes, FileLog log, SteamRestartTimings? timings = null)
{
    private const string SteamProcess = "steam";
    private const string HelperProcess = "steamwebhelper";
    private const string DotaProcess = "dota2";
    private readonly SteamRestartTimings _timings = timings ?? SteamRestartTimings.Default;

    /// <summary>Only "-" options: Steam runs "+" commands from its own command line itself.</summary>
    public static IReadOnlyList<string> CloseArguments { get; } = ["-shutdown"];

    /// <summary>
    /// Runs the change between closing and starting Steam; Steam is started again also when the
    /// change throws. Blocks while Steam closes, so it is called off the UI thread.
    /// </summary>
    public SteamRestartResult<T> Around<T>(GameState game, Func<T> change)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(change);
        if (game.Steam is not SteamInfo steam)
        {
            return new(SteamRestartProblem.NoSteam, default!);
        }

        if (game.DotaRunning || processes.IsRunning(DotaProcess))
        {
            return new(SteamRestartProblem.DotaRunning, default!);
        }

        string exe = Path.Combine(steam.Root, "steam.exe");
        if (!Close(exe))
        {
            return new(SteamRestartProblem.DidNotClose, default!);
        }

        T result;
        try
        {
            result = change();
        }
        catch
        {
            Start(exe);
            throw;
        }

        return new(Start(exe) ? null : SteamRestartProblem.DidNotStart, result);
    }

    private bool Close(string exe)
    {
        try
        {
            starter.Start(exe, CloseArguments);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.Error($"Could not ask Steam to close through {exe}", ex);
            return false;
        }

        log.Info($"Steam asked to close: {string.Join(' ', CloseArguments)}");
        var clock = Stopwatch.StartNew();
        while (processes.IsRunning(SteamProcess))
        {
            if (clock.Elapsed >= _timings.Close)
            {
                log.Warning($"Steam is still running {_timings.Close.TotalSeconds:F0} s after it was asked to close, nothing is changed");
                return false;
            }

            Thread.Sleep(_timings.Poll);
        }

        log.Info($"Steam closed in {clock.Elapsed.TotalSeconds:F1} s");

        // On 27.10.2026 a Steam started in the same second as the old one logged its shutdown never
        // named the signed-in account in the registry. Not proven to be the cause: the log says
        // whether the old web helpers were still finishing.
        clock.Restart();
        while (processes.IsRunning(HelperProcess) && clock.Elapsed < _timings.Helpers)
        {
            Thread.Sleep(_timings.Poll);
        }

        if (processes.IsRunning(HelperProcess))
        {
            log.Warning($"Steam web helpers still run {_timings.Helpers.TotalSeconds:F0} s after Steam closed, the change goes on");
        }
        else if (clock.Elapsed >= _timings.Poll)
        {
            log.Info($"Steam web helpers closed {clock.Elapsed.TotalSeconds:F1} s after Steam");
        }

        return true;
    }

    private bool Start(string exe)
    {
        try
        {
            starter.Start(exe, []);
            log.Info("Steam started again");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.Error($"Could not start Steam again from {exe}", ex);
            return false;
        }
    }
}
