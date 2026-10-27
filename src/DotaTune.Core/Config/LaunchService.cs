using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Game;
using DotaTune.Core.Steam;

namespace DotaTune.Core.Config;

public enum LaunchRefusal
{
    /// <summary>Steam rewrites localconfig.vdf from memory on exit, so a change made now would be lost.</summary>
    SteamRunning,
    NoAccount,

    /// <summary>The account has no Dota block in localconfig.vdf: the game was never started on it.</summary>
    NoDotaBlock,

    /// <summary>The line was changed in Steam after DotaTune wrote it, so "put back" left it as it is.</summary>
    ChangedInSteam,
}

/// <summary>Launch options of one account before DotaTune first changed them, and what it wrote last.</summary>
public sealed record LaunchChange(uint AccountId, string LocalConfigPath, string Before, string Written, DateTimeOffset WrittenAt);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<LaunchChange>))]
internal sealed partial class LaunchJsonContext : JsonSerializerContext;

/// <summary>launch.json next to the settings: the launch lines DotaTune replaced, one per account.</summary>
public sealed class LaunchChangeStore(string file, FileLog log)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public IReadOnlyList<LaunchChange> Load()
    {
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file, Utf8NoBom), LaunchJsonContext.Default.ListLaunchChange) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Error($"Launch state {file} is unreadable", ex);
            throw new InvalidDataException($"{file} is unreadable: {ex.Message}", ex);
        }
    }

    public void Save(IReadOnlyList<LaunchChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            File.Delete(file);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize([.. changes], LaunchJsonContext.Default.ListLaunchChange), Utf8NoBom);
        File.Move(temp, file, overwrite: true);
    }
}

/// <summary>Changes the Dota launch options of the signed-in account and can put the first line back.</summary>
public sealed class LaunchService(LaunchChangeStore store, FileLog log)
{
    public LaunchChange? ChangeOf(uint accountId) => store.Load().FirstOrDefault(c => c.AccountId == accountId);

    /// <summary>Whether a line written by DotaTune is waiting to be put back in any account.</summary>
    public bool HasChanges => store.Load().Count > 0;

    /// <summary>Writes the line; the line from before the first change is kept for "put back". Null when done.</summary>
    public LaunchRefusal? Apply(GameState game, string options)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(options);
        if (game.SteamRunning)
        {
            return LaunchRefusal.SteamRunning;
        }

        if (game.Steam?.Account is not SteamAccount account)
        {
            return LaunchRefusal.NoAccount;
        }

        string file = LaunchOptions.LocalConfigPath(game.Steam.Root, account.AccountId);
        string? current = File.Exists(file) ? LaunchOptions.Read(game.Steam.Root, account.AccountId) : null;
        if (current is null)
        {
            return LaunchRefusal.NoDotaBlock;
        }

        List<LaunchChange> changes = [.. store.Load()];
        LaunchChange? earlier = changes.FirstOrDefault(c => c.AccountId == account.AccountId);

        // A line changed in Steam since the last write is what "put back" returns to from now on.
        string before = earlier is not null && string.Equals(current, earlier.Written, StringComparison.Ordinal) ? earlier.Before : current;
        List<LaunchChange> updated = [.. changes.Where(c => c.AccountId != account.AccountId)];
        if (!string.Equals(options, before, StringComparison.Ordinal))
        {
            updated.Add(new LaunchChange(account.AccountId, file, before, options, DateTimeOffset.Now));
        }

        // The record goes first: a line DotaTune changed without one could never be put back.
        store.Save(updated);
        bool written;
        try
        {
            written = LocalConfigEditor.SetLaunchOptions(file, options);
        }
        catch
        {
            store.Save(changes);
            throw;
        }

        if (!written)
        {
            store.Save(changes);
            return LaunchRefusal.NoDotaBlock;
        }

        log.Info($"Launch options of {account.AccountId}: \"{current}\" -> \"{options}\", first line \"{before}\"");
        return null;
    }

    /// <summary>
    /// Puts back the line from before the first change. Null when done; ChangedInSteam when the
    /// line was changed in Steam since and stays, the record is forgotten either way.
    /// </summary>
    public LaunchRefusal? Restore(GameState game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Steam?.Account is not SteamAccount account)
        {
            return LaunchRefusal.NoAccount;
        }

        return RestoreWhere(game, c => c.AccountId == account.AccountId);
    }

    /// <summary>Uninstall hook: every account DotaTune changed.</summary>
    public LaunchRefusal? RestoreAll(GameState game) => RestoreWhere(game, _ => true);

    private LaunchRefusal? RestoreWhere(GameState game, Func<LaunchChange, bool> which)
    {
        List<LaunchChange> changes = [.. store.Load()];
        List<LaunchChange> chosen = [.. changes.Where(which)];
        if (chosen.Count == 0)
        {
            return null;
        }

        if (game.SteamRunning)
        {
            return LaunchRefusal.SteamRunning;
        }

        LaunchRefusal? result = null;
        foreach (LaunchChange change in chosen)
        {
            // Read by the remembered path: it does not depend on finding Steam again.
            string? now = LaunchOptions.ReadFile(change.LocalConfigPath);
            if (string.Equals(now, change.Written, StringComparison.Ordinal))
            {
                LocalConfigEditor.SetLaunchOptions(change.LocalConfigPath, change.Before);
                log.Info($"Launch options of {change.AccountId} put back: \"{change.Before}\"");
            }
            else
            {
                log.Info($"Launch options of {change.AccountId} were changed in Steam since, left as \"{now}\"");
                result = LaunchRefusal.ChangedInSteam;
            }

            changes.Remove(change);
            store.Save(changes);
        }

        return result;
    }
}
