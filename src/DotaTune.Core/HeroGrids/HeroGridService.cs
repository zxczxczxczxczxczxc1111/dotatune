using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotaTune.Core.Config;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Game;
using DotaTune.Core.Steam;

namespace DotaTune.Core.HeroGrids;

public enum GridRefusal
{
    /// <summary>The game keeps the grids in memory and writes them back over a change.</summary>
    DotaRunning,
    NoSteam,
    NoAccount,

    /// <summary>The account's file is not the game's JSON; it is left alone.</summary>
    BrokenFile,

    /// <summary>Steam does not list the account's file, so the game would not read layouts put into it.</summary>
    NotSeenByGame,
}

/// <summary>A layout to have in the game; a null layout keeps the one there as it is.</summary>
public sealed record GridChoice(string Name, HeroGridLayout? Layout);

/// <summary>A layout of DotaTune found in the game; null when it no longer reads as a valid one.</summary>
public sealed record InstalledGrid(string Name, HeroGridLayout? Layout);

/// <summary>Refusal, or how many layouts went in and came out.</summary>
public sealed record GridResult(GridRefusal? Refusal, int Added, int Removed, string? Detail = null);

/// <summary>The hero grids of the account Dota settings belong to, as the page shows them.</summary>
/// <param name="SeenByGame">Steam lists the file, see <see cref="HeroGridService.SeenByGame"/>.</param>
public sealed record GridAccountState(
    GridRefusal? Problem,
    string? Detail,
    SteamAccount? Account,
    bool CloudEnabled,
    bool SeenByGame,
    IReadOnlyList<InstalledGrid> Installed);

/// <summary>An account DotaTune changed: whether it had the file then; its copy is next to the record.</summary>
public sealed record GridBackup(uint AccountId, bool Existed, DateTimeOffset At);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<GridBackup>))]
internal sealed partial class GridJsonContext : JsonSerializerContext;

/// <summary>herogrids.json next to the settings and the copies of the accounts' files.</summary>
public sealed class GridBackupStore(string file, string folder, FileLog log)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string CopyOf(uint accountId) =>
        Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"backup-{accountId}.json"));

    public IReadOnlyList<GridBackup> Load()
    {
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file, Utf8NoBom), GridJsonContext.Default.ListGridBackup) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Error($"Hero grid state {file} is unreadable", ex);
            throw new InvalidDataException($"{file} is unreadable: {ex.Message}", ex);
        }
    }

    public void Save(IReadOnlyList<GridBackup> backups)
    {
        ArgumentNullException.ThrowIfNull(backups);
        if (backups.Count == 0)
        {
            File.Delete(file);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize([.. backups], GridJsonContext.Default.ListGridBackup), Utf8NoBom);
        File.Move(temp, file, overwrite: true);
    }
}

/// <summary>
/// Puts ready-made layouts into hero_grid_config.json of an account and takes them out again,
/// touching only the layouts tagged with <see cref="GridMark"/>. Before the first change the
/// file is copied; once none of ours is left the copy goes, and a file DotaTune created goes too.
/// </summary>
public sealed class HeroGridService(GridBackupStore store, FileLog log)
{
    private const string TempSuffix = ".dotatune.tmp";

    public static string PathOf(string steamRoot, uint accountId) =>
        TransferFiles.Full(TransferFiles.GameFolder(steamRoot, accountId), TransferFiles.HeroGridPath);

    /// <summary>Whether the game of the account would read its grid file, see <see cref="TransferFiles.SteamLists"/>.</summary>
    public static bool SeenByGame(string steamRoot, uint accountId) =>
        TransferFiles.SteamLists(TransferFiles.GameFolder(steamRoot, accountId), TransferFiles.HeroGridPath);

    /// <summary>The layouts of DotaTune in the file of the account Dota settings belong to.</summary>
    public static GridAccountState Read(GameState game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Steam is not { } steam)
        {
            return new GridAccountState(GridRefusal.NoSteam, null, null, false, false, []);
        }

        if (steam.Account is not { } account || !Directory.Exists(TransferFiles.GameFolder(steam.Root, account.AccountId)))
        {
            return new GridAccountState(GridRefusal.NoAccount, null, steam.Account, false, false, []);
        }

        bool cloud = TransferService.CloudEnabled(steam.Root, account.AccountId);
        bool seen = SeenByGame(steam.Root, account.AccountId);
        string path = PathOf(steam.Root, account.AccountId);
        if (!File.Exists(path))
        {
            return new GridAccountState(null, null, account, cloud, seen, []);
        }

        try
        {
            IReadOnlyList<InstalledGrid> installed =
            [
                .. GridFile.Parse(File.ReadAllBytes(path)).Entries
                    .Where(e => e.IsOurs)
                    .Select(e => new InstalledGrid(GridMark.Strip(e.Name!), e.Layout)),
            ];
            return new GridAccountState(null, null, account, cloud, seen, installed);
        }
        catch (InvalidDataException ex)
        {
            return new GridAccountState(GridRefusal.BrokenFile, $"{path}: {ex.Message}", account, cloud, seen, []);
        }
    }

    /// <summary>Accounts that have DotaTune layouts in them, for the uninstall hook.</summary>
    /// <exception cref="InvalidDataException">herogrids.json is unreadable.</exception>
    public IReadOnlyList<uint> Changed() => [.. store.Load().Select(b => b.AccountId)];

    /// <summary>
    /// Makes our layouts in the account's file exactly the wanted ones, in that order for the new
    /// ones. A layout already there with the same content stays where it is, byte for byte.
    /// </summary>
    /// <exception cref="InvalidDataException">herogrids.json is unreadable.</exception>
    public GridResult Apply(GameState game, uint accountId, IReadOnlyList<GridChoice> wanted)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(wanted);
        if (game.DotaRunning)
        {
            return new GridResult(GridRefusal.DotaRunning, 0, 0);
        }

        if (game.Steam is not { } steam)
        {
            return new GridResult(GridRefusal.NoSteam, 0, 0);
        }

        if (!Directory.Exists(TransferFiles.GameFolder(steam.Root, accountId)))
        {
            // The account's folder is gone and our layouts with it: nothing is left to put back.
            Forget(accountId);
            return new GridResult(GridRefusal.NoAccount, 0, 0);
        }

        string path = PathOf(steam.Root, accountId);
        if (File.Exists(path + TempSuffix))
        {
            // Left by a write that was cut short; it must not stay in the folder Steam Cloud syncs.
            File.Delete(path + TempSuffix);
        }

        byte[]? before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        GridFile grid;
        try
        {
            grid = GridFile.Parse(before ?? GridFile.Empty);
        }
        catch (InvalidDataException ex)
        {
            log.Error($"Hero grids of {accountId} are not changed, the file is broken", ex);
            return new GridResult(GridRefusal.BrokenFile, 0, 0, $"{path}: {ex.Message}");
        }

        Dictionary<string, GridChoice> byName = wanted.DistinctBy(c => c.Name, StringComparer.Ordinal).ToDictionary(c => c.Name, StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var remove = new HashSet<GridEntry>();
        foreach (GridEntry entry in grid.Entries.Where(e => e.IsOurs))
        {
            string name = GridMark.Strip(entry.Name!);
            bool same = byName.TryGetValue(name, out GridChoice? choice)
                && !kept.Contains(name)
                && (choice.Layout is null || entry.Layout?.SameAs(choice.Layout) == true);
            if (same)
            {
                kept.Add(name);
            }
            else
            {
                remove.Add(entry);
            }
        }

        List<HeroGridLayout> add =
        [
            .. wanted.DistinctBy(c => c.Name, StringComparer.Ordinal).Where(c => !kept.Contains(c.Name)).Select(c => c.Layout).OfType<HeroGridLayout>(),
        ];
        if (remove.Count == 0 && add.Count == 0)
        {
            return new GridResult(null, 0, 0);
        }

        // Taking ours out always goes ahead; putting in waits until the game can read the file.
        if (add.Count > 0 && !SeenByGame(steam.Root, accountId))
        {
            log.Info($"Hero grids of {accountId} are not changed: Steam does not list the file, the game would not read it");
            return new GridResult(GridRefusal.NotSeenByGame, 0, 0);
        }

        byte[] after = grid.Edit(remove, add);
        List<GridBackup> backups = [.. store.Load()];
        GridBackup? backup = backups.FirstOrDefault(b => b.AccountId == accountId);
        string copy = store.CopyOf(accountId);

        // No layout of ours in the file: whatever happened to it meanwhile (a transfer put back,
        // a preset), this is the player's file before the first change now.
        if (backup is null || !grid.Entries.Any(e => e.IsOurs))
        {
            if (backup is not null)
            {
                backups.Remove(backup);
            }

            if (before is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.WriteAllBytes(copy, before);
            }
            else if (File.Exists(copy))
            {
                File.Delete(copy);
            }

            backup = new GridBackup(accountId, before is not null, DateTimeOffset.Now);
            backups.Add(backup);
            store.Save(backups);
        }

        if (kept.Count > 0 || add.Count > 0)
        {
            WriteFile(path, after);
        }
        else
        {
            // None of ours is left: the player's file as it was, and nothing of ours remembers it.
            if (!backup.Existed && after.AsSpan().SequenceEqual(GridFile.Empty))
            {
                File.Delete(path);
            }
            else
            {
                WriteFile(path, after);
                if (!backup.Existed || !File.Exists(copy) || !after.AsSpan().SequenceEqual(File.ReadAllBytes(copy)))
                {
                    log.Info($"Hero grids of {accountId} differ from the copy before the first change: the game or the player changed them since");
                }
            }

            backups.Remove(backup);
            store.Save(backups);
            if (File.Exists(copy))
            {
                File.Delete(copy);
            }
        }

        log.Info($"Hero grids of {accountId}: {add.Count} put in, {remove.Count} taken out");
        return new GridResult(null, add.Count, remove.Count);
    }

    /// <summary>Takes every layout of DotaTune out of the account's file.</summary>
    /// <exception cref="InvalidDataException">herogrids.json is unreadable.</exception>
    public GridResult RemoveAll(GameState game, uint accountId) => Apply(game, accountId, []);

    private void Forget(uint accountId)
    {
        List<GridBackup> backups = [.. store.Load()];
        if (backups.RemoveAll(b => b.AccountId == accountId) == 0)
        {
            return;
        }

        store.Save(backups);
        string copy = store.CopyOf(accountId);
        if (File.Exists(copy))
        {
            File.Delete(copy);
        }

        log.Info($"Hero grids of {accountId} forgotten: the account has no Dota folder any more");
    }

    private static void WriteFile(string file, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + TempSuffix;
        File.WriteAllBytes(temp, content);
        File.Move(temp, file, overwrite: true);
    }
}
