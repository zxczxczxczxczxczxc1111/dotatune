using System.Diagnostics;
using DotaTune.Core.Steam;

namespace DotaTune.Core.Game;

public enum GameProblem
{
    SteamNotFound,
    DotaNotFound,
    NoAccount,
    UnreadableFile,
}

/// <summary>Everything DotaTune knows about the game at one moment. Reading never writes anything.</summary>
public sealed record GameState(
    SteamInfo? Steam,
    DotaInstall? Dota,
    BootConfig? Boot,
    string? LaunchOptions,
    IReadOnlyList<LanguageFolder> Folders,
    PackDecision? Decision,
    bool SteamRunning,
    bool DotaRunning,
    GameProblem? Problem,
    string? ProblemDetail);

public interface IProcessProbe
{
    bool IsRunning(string processName);
}

public sealed class SystemProcessProbe : IProcessProbe
{
    public bool IsRunning(string processName)
    {
        Process[] found = Process.GetProcessesByName(processName);
        foreach (Process process in found)
        {
            process.Dispose();
        }

        return found.Length > 0;
    }
}

public sealed class GameStateReader(ISteamRegistry registry, IProcessProbe processes)
{
    public GameState Read()
    {
        bool steamRunning = processes.IsRunning("steam");
        bool dotaRunning = processes.IsRunning("dota2");
        GameState Fail(GameProblem problem, SteamInfo? steam = null, DotaInstall? dota = null, string? detail = null) =>
            new(steam, dota, null, null, [], null, steamRunning, dotaRunning, problem, detail);

        try
        {
            SteamInfo? steam = SteamLocator.Locate(registry);
            if (steam is null)
            {
                return Fail(GameProblem.SteamNotFound);
            }

            DotaInstall? dota = DotaInstall.Find(steam.Libraries);
            if (dota is null)
            {
                return Fail(GameProblem.DotaNotFound, steam);
            }

            BootConfig? boot = BootConfig.Read(dota.GameRoot);
            IReadOnlyList<LanguageFolder> folders = LanguageFolders.Scan(dota.GameRoot);
            string? options = steam.Account is null ? null : Steam.LaunchOptions.Read(steam.Root, steam.Account.AccountId);
            PackDecision decision = PackPlanner.Decide(boot, Steam.LaunchOptions.LanguageOverride(options), folders);

            return new GameState(
                steam,
                dota,
                boot,
                options,
                folders,
                decision,
                steamRunning,
                dotaRunning,
                steam.Account is null ? GameProblem.NoAccount : null,
                null);
        }
        catch (KeyValuesException ex)
        {
            return Fail(GameProblem.UnreadableFile, detail: ex.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(GameProblem.UnreadableFile, detail: ex.Message);
        }
    }
}
