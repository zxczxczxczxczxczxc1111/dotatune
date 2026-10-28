using DotaTune.Core.Config;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Game;
using DotaTune.Core.HeroGrids;
using DotaTune.Core.Pack;
using DotaTune.Core.Steam;

namespace DotaTune.Core.Reset;

/// <summary>What DotaTune can have changed in the game, in the order a reset takes it back.</summary>
public enum ResetPart
{
    Pack,
    Autoexec,

    /// <summary>
    /// Settings and presets copied into accounts. Put back before launch options and hero grids:
    /// a copy from before a transfer can hold a launch line or layouts DotaTune wrote earlier.
    /// </summary>
    Transfers,
    LaunchOptions,
    HeroGrids,
}

/// <summary>Why a part was not taken back, or not all of it.</summary>
public enum ResetProblem
{
    DotaRunning,
    SteamRunning,
    NoSteam,
    NoGame,

    /// <summary>A DotaTune mark in autoexec.cfg is missing or doubled; <see cref="ResetStep.Detail"/> is the file.</summary>
    BrokenAutoexec,

    /// <summary>The launch line was changed in Steam after DotaTune wrote it, so it stays; the record is dropped.</summary>
    LaunchChangedInSteam,

    /// <summary>Files or the launch line changed after a transfer stay as they are; the record is dropped.</summary>
    TransferChangedSince,

    /// <summary>Files whose copies are gone stay as they are; the record is dropped.</summary>
    TransferCopiesLost,

    /// <summary>An account's hero grid file is not JSON and stays; <see cref="ResetStep.Detail"/> says which.</summary>
    BrokenGrids,

    /// <summary>A file could not be read or written; <see cref="ResetStep.Detail"/> is the error.</summary>
    Failed,
}

/// <param name="Problem">Null when the part was taken back.</param>
public sealed record ResetStep(ResetPart Part, ResetProblem? Problem, string? Detail = null);

/// <summary>Takes back everything DotaTune changed in the game, by its own records of what it wrote.</summary>
public sealed class GameReset(PackService packs, AutoexecService autoexec, TransferService transfers, LaunchService launch, HeroGridService grids, FileLog log)
{
    /// <summary>
    /// The parts DotaTune changed, in reset order. A record that cannot be read counts as a change,
    /// so the reset tries it and tells what went wrong. A pack with the DotaTune marker counts even
    /// without a record: the record is lost when the program's data folder is.
    /// </summary>
    public IReadOnlyList<ResetPart> Pending(GameState game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return [.. Enum.GetValues<ResetPart>().Where(part => Changed(part, game))];
    }

    /// <summary>Takes back every changed part. The game is read before each, because taking a part back changes it.</summary>
    /// <param name="skip">Parts to leave as they are; the uninstall hook keeps the settings copied between accounts.</param>
    public IReadOnlyList<ResetStep> Run(Func<GameState> readGame, params ResetPart[] skip)
    {
        ArgumentNullException.ThrowIfNull(readGame);
        ArgumentNullException.ThrowIfNull(skip);
        List<ResetStep> steps = [];
        foreach (ResetPart part in Pending(readGame()).Except(skip))
        {
            ResetStep step;
            try
            {
                step = Take(part, readGame());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or KeyValuesException)
            {
                log.Error($"Reset of {part} failed", ex);
                step = new ResetStep(part, ResetProblem.Failed, ex.Message);
            }

            steps.Add(step);
        }

        log.Info(steps.Count == 0
            ? "Reset: nothing to take back"
            : $"Reset: {string.Join(", ", steps.Select(s => $"{s.Part} {s.Problem?.ToString() ?? "done"}"))}");
        return steps;
    }

    private bool Changed(ResetPart part, GameState game)
    {
        try
        {
            return part switch
            {
                ResetPart.Pack => packs.Current is not null || MarkedPaks(game).Any(),
                ResetPart.Autoexec => autoexec.Current is not null,
                ResetPart.Transfers => transfers.Restorable().Count > 0,
                ResetPart.LaunchOptions => launch.HasChanges,
                ResetPart.HeroGrids => grids.Changed().Count > 0,
                _ => throw new InvalidOperationException($"Unknown reset part {part}"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log.Error($"The record of {part} is unreadable, the reset will try it", ex);
            return true;
        }
    }

    private ResetStep Take(ResetPart part, GameState game) => part switch
    {
        ResetPart.Pack => TakePack(game),
        ResetPart.Autoexec => TakeAutoexec(game),
        ResetPart.Transfers => TakeTransfers(game),
        ResetPart.LaunchOptions => TakeLaunch(game),
        ResetPart.HeroGrids => TakeGrids(game),
        _ => throw new InvalidOperationException($"Unknown reset part {part}"),
    };

    private ResetStep TakePack(GameState game)
    {
        if (game.DotaRunning)
        {
            return new ResetStep(ResetPart.Pack, ResetProblem.DotaRunning);
        }

        string? recorded = packs.Current?.PakPath;
        packs.Remove(game);

        // Packs with the marker that no record points to: left by an earlier install whose record was lost.
        foreach (string pak in MarkedPaks(game).Where(p => !string.Equals(p, recorded, StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(pak);
            log.Info($"Pack without a record removed: {pak}");
        }

        return new ResetStep(ResetPart.Pack, null);
    }

    private static IEnumerable<string> MarkedPaks(GameState game) =>
        game.Folders.SelectMany(f => f.Paks.Where(p => p.Owner == PakOwner.DotaTune).Select(p => Path.Combine(f.Path, p.FileName)));

    private ResetStep TakeAutoexec(GameState game)
    {
        string? file = autoexec.Current?.AutoexecPath;
        return autoexec.Remove(game) switch
        {
            null => new ResetStep(ResetPart.Autoexec, null),
            ConfigRefusal.DotaRunning => new ResetStep(ResetPart.Autoexec, ResetProblem.DotaRunning),
            ConfigRefusal.NoGame => new ResetStep(ResetPart.Autoexec, ResetProblem.NoGame),
            ConfigRefusal.BrokenMarks => new ResetStep(ResetPart.Autoexec, ResetProblem.BrokenAutoexec, file),
            ConfigRefusal other => new ResetStep(ResetPart.Autoexec, ResetProblem.Failed, other.ToString()),
        };
    }

    /// <summary>Every account is tried; the first account that stays gives the problem.</summary>
    private ResetStep TakeTransfers(GameState game)
    {
        ResetStep? first = null;
        foreach (uint account in transfers.Restorable())
        {
            TransferRefusal? refusal = transfers.Restore(game, account).Refusal;
            ResetProblem? problem = refusal switch
            {
                // Nothing to copy: the account has no record any more, so there is nothing left to put back.
                null or TransferRefusal.NothingToCopy => null,
                TransferRefusal.DotaRunning => ResetProblem.DotaRunning,
                TransferRefusal.SteamRunning => ResetProblem.SteamRunning,
                TransferRefusal.NoSteam => ResetProblem.NoSteam,
                TransferRefusal.ChangedSince => ResetProblem.TransferChangedSince,
                TransferRefusal.CopiesLost => ResetProblem.TransferCopiesLost,
                _ => ResetProblem.Failed,
            };
            if (problem is not null)
            {
                first ??= new ResetStep(ResetPart.Transfers, problem, problem == ResetProblem.Failed ? refusal.ToString() : null);
            }
        }

        return first ?? new ResetStep(ResetPart.Transfers, null);
    }

    private ResetStep TakeLaunch(GameState game) => launch.RestoreAll(game) switch
    {
        null => new ResetStep(ResetPart.LaunchOptions, null),
        LaunchRefusal.SteamRunning => new ResetStep(ResetPart.LaunchOptions, ResetProblem.SteamRunning),
        LaunchRefusal.ChangedInSteam => new ResetStep(ResetPart.LaunchOptions, ResetProblem.LaunchChangedInSteam),
        LaunchRefusal other => new ResetStep(ResetPart.LaunchOptions, ResetProblem.Failed, other.ToString()),
    };

    /// <summary>Every account is tried; the first account that stays gives the problem.</summary>
    private ResetStep TakeGrids(GameState game)
    {
        ResetStep? first = null;
        foreach (uint account in grids.Changed())
        {
            GridResult result = grids.RemoveAll(game, account);
            ResetProblem? problem = result.Refusal switch
            {
                // No account: its folder is gone and the layouts with it; the record is dropped.
                null or GridRefusal.NoAccount => null,
                GridRefusal.DotaRunning => ResetProblem.DotaRunning,
                GridRefusal.NoSteam => ResetProblem.NoSteam,
                GridRefusal.BrokenFile => ResetProblem.BrokenGrids,
                _ => ResetProblem.Failed,
            };
            if (problem is not null)
            {
                first ??= new ResetStep(ResetPart.HeroGrids, problem, result.Detail ?? result.Refusal.ToString());
            }
        }

        return first ?? new ResetStep(ResetPart.HeroGrids, null);
    }
}
