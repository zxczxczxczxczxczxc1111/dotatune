using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Game;
using DotaTune.Core.Pack;
using DotaTune.Core.Steam;

namespace DotaTune.Core.Config;

/// <summary>What can be carried from one account to another; personal files are in none of them.</summary>
public enum TransferGroup
{
    Hotkeys,
    GameSettings,
    ConsoleBinds,
    HeroGrids,
    Builds,
    Misc,
    Graphics,
    LaunchOptions,
}

public enum TransferRefusal
{
    DotaRunning,

    /// <summary>Launch options were chosen, and Steam rewrites them from memory on exit.</summary>
    SteamRunning,
    NoSteam,
    SameAccount,
    NoSuchAccount,
    NothingToCopy,

    /// <summary>Only hero grids were to be written, and the game of the target would not read them.</summary>
    GridsNotSeen,

    /// <summary>Put back, except the files or the launch line changed after the transfer: those stay as they are.</summary>
    ChangedSince,
}

/// <summary>An account of this computer that has started Dota.</summary>
public sealed record GameAccount(uint AccountId, string Name, bool CloudEnabled);

/// <summary>Refusal, or how many files and lines were written.</summary>
/// <param name="GridsSkipped">Hero grids were left out: Steam does not list the target's file, see <see cref="TransferFiles.SteamLists"/>.</param>
public sealed record TransferResult(TransferRefusal? Refusal, int Written, bool GridsSkipped = false);

/// <summary>A file of the account before the first transfer into it: its copy, or that it did not exist.</summary>
/// <param name="Written">SHA-1 of what the last transfer wrote there. Null in records of earlier builds, which are put back unchecked.</param>
public sealed record TransferEntry(string RelativePath, string? Backup, string? Written = null);

/// <summary>Everything the transfers into one account replaced, for "put back".</summary>
/// <param name="LaunchWritten">The line the last transfer wrote. Null in records of earlier builds, which are put back unchecked.</param>
public sealed record TransferBackup(
    uint AccountId, IReadOnlyList<TransferEntry> Files, bool LaunchTouched, string? LaunchBefore, DateTimeOffset At, string? LaunchWritten = null);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<TransferBackup>))]
internal sealed partial class TransferJsonContext : JsonSerializerContext;

/// <summary>
/// Which files of userdata\&lt;id&gt;\570 each group carries. Settings and console binds are
/// written into all three copies the game keeps, with the line ends it writes: LF in remote,
/// CRLF in local.
/// </summary>
public static class TransferFiles
{
    /// <summary>Tag of hero grids carried by DotaTune; the next transfer replaces only these.</summary>
    public const string HeroGridMark = " #DotaTune";

    public const string HeroGridPath = "remote/cfg/hero_grid_config.json";

    public const string HotkeysPath = "remote/cfg/dotakeys_personal.lst";

    private static readonly string[] Convars = ["remote/user_convars.vcfg", "local/cfg/user_convars_0_slot0.vcfg", "local/cfg/user_convars_0_slot0.vcfg_lastclouded"];
    private static readonly string[] Keys = ["remote/user_keys.vcfg", "local/cfg/user_keys_0_slot0.vcfg", "local/cfg/user_keys_0_slot0.vcfg_lastclouded"];

    private static readonly JsonSerializerOptions GridJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<TransferGroup> All { get; } = Enum.GetValues<TransferGroup>();

    /// <summary>Files to write into the target folder, with their contents; paths with forward slashes.</summary>
    /// <exception cref="InvalidDataException">A hero grid file is not the JSON the game writes.</exception>
    public static IReadOnlyList<(string Path, byte[] Content)> Plan(string sourceFolder, string targetFolder, IEnumerable<TransferGroup> groups)
    {
        var files = new List<(string, byte[])>();
        foreach (TransferGroup group in groups.Distinct())
        {
            switch (group)
            {
                case TransferGroup.Hotkeys:
                    Plain(HotkeysPath);
                    break;
                case TransferGroup.GameSettings:
                    Copies(Convars);
                    break;
                case TransferGroup.ConsoleBinds:
                    Copies(Keys);
                    foreach (int slot in new[] { 1, 2, 3 })
                    {
                        Plain(string.Create(CultureInfo.InvariantCulture, $"local/cfg/user_keys_0_slot{slot}.vcfg"));
                        Plain(string.Create(CultureInfo.InvariantCulture, $"local/cfg/user_keys_0_slot{slot}.vcfg_lastclouded"));
                    }

                    break;
                case TransferGroup.HeroGrids:
                    if (File.Exists(Full(sourceFolder, HeroGridPath)))
                    {
                        string target = Full(targetFolder, HeroGridPath);
                        files.Add((HeroGridPath, MergeHeroGrids(
                            File.ReadAllBytes(Full(sourceFolder, HeroGridPath)),
                            File.Exists(target) ? File.ReadAllBytes(target) : null)));
                    }

                    break;
                case TransferGroup.Builds:
                    Plain("remote/cfg/herobuilds.cfg");
                    Plain("remote/cfg/hero_facet_config.cfg");
                    string guides = Full(sourceFolder, "remote/guides");
                    if (Directory.Exists(guides))
                    {
                        foreach (string guide in Directory.EnumerateFiles(guides, "*.build").Order(StringComparer.OrdinalIgnoreCase))
                        {
                            Plain("remote/guides/" + Path.GetFileName(guide));
                        }
                    }

                    break;
                case TransferGroup.Misc:
                    Plain("remote/scripts/item_suggest_preference.txt");
                    Plain("remote/scripts/control_groups.txt");
                    Plain("remote/cfg/chat.cfg");
                    Plain("remote/scripts/lobby_settings.txt");
                    break;
                case TransferGroup.Graphics:
                    Plain("local/cfg/video.txt");
                    Plain("local/cfg/machine_convars.vcfg");
                    break;
                case TransferGroup.LaunchOptions:
                    // Not a file of the game folder: the service writes it into localconfig.vdf.
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(groups), group, null);
            }
        }

        return files;

        void Plain(string path)
        {
            string file = Full(sourceFolder, path);
            if (File.Exists(file))
            {
                files.Add((path, File.ReadAllBytes(file)));
            }
        }

        // The newest of the three copies goes into all three.
        void Copies(string[] paths)
        {
            if (Newest(sourceFolder, paths) is byte[] content)
            {
                files.AddRange(IntoCopies(paths, content));
            }
        }
    }

    /// <summary>The game settings of the account from the newest of its three copies; null when it has none.</summary>
    public static byte[]? GameSettings(string folder) => Newest(folder, Convars);

    /// <summary>Game settings for all three copies, with the line ends the game writes.</summary>
    public static IReadOnlyList<(string Path, byte[] Content)> GameSettingsCopies(byte[] content) => IntoCopies(Convars, content);

    /// <summary>Hero grids written anew after a check that they are the game's JSON.</summary>
    /// <exception cref="InvalidDataException">Not a JSON object with a "configs" list.</exception>
    public static byte[] NormalizeHeroGrids(byte[] json) => Encoding.UTF8.GetBytes(ParseGrids(json).ToJsonString(GridJson));

    private static byte[]? Newest(string folder, string[] paths)
    {
        FileInfo? newest = paths.Select(p => new FileInfo(Full(folder, p)))
            .Where(f => f.Exists)
            .MaxBy(f => f.LastWriteTimeUtc);
        return newest is null ? null : File.ReadAllBytes(newest.FullName);
    }

    private static List<(string, byte[])> IntoCopies(string[] paths, byte[] content)
    {
        // Only CRLF and LF: ReplaceLineEndings would also take byte 0x85, a part of UTF-8 Cyrillic.
        string text = Encoding.Latin1.GetString(content).Replace("\r\n", "\n", StringComparison.Ordinal);
        return
        [
            .. paths.Select(path => (path, Encoding.Latin1.GetBytes(
                path.StartsWith("local/", StringComparison.Ordinal) ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text))),
        ];
    }

    /// <summary>
    /// The target's grids stay; the source's grids are added with <see cref="HeroGridMark"/>,
    /// and the grids a previous transfer added are replaced.
    /// </summary>
    /// <exception cref="InvalidDataException">Either file is not a JSON object with a "configs" list.</exception>
    public static byte[] MergeHeroGrids(byte[] source, byte[]? target)
    {
        JsonObject from = ParseGrids(source);
        JsonObject into = target is null
            ? new JsonObject { ["version"] = from["version"]?.DeepClone() ?? 3, ["configs"] = new JsonArray() }
            : ParseGrids(target);
        var configs = (JsonArray)into["configs"]!;
        foreach (JsonNode? old in configs.Where(c => NameOf(c).EndsWith(HeroGridMark, StringComparison.Ordinal)).ToList())
        {
            configs.Remove(old);
        }

        foreach (JsonNode? config in (JsonArray)from["configs"]!)
        {
            if (config is not JsonObject grid)
            {
                continue;
            }

            var copy = (JsonObject)grid.DeepClone();
            string name = NameOf(grid);
            copy["config_name"] = (name.EndsWith(HeroGridMark, StringComparison.Ordinal) ? name[..^HeroGridMark.Length] : name) + HeroGridMark;
            configs.Add(copy);
        }

        return Encoding.UTF8.GetBytes(into.ToJsonString(GridJson));
    }

    /// <summary>userdata\&lt;id&gt;\570 of the account.</summary>
    public static string GameFolder(string steamRoot, uint accountId) =>
        Path.Combine(steamRoot, "userdata", accountId.ToString(CultureInfo.InvariantCulture), LaunchOptions.DotaAppId);

    public static string Full(string folder, string relativePath) =>
        Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Whether Steam lists a remote file of the account in its remotecache.vdf. The game reads
    /// remote files through Steam's remote storage, which knows only files the game once saved
    /// itself: an unlisted file is not read, and the game's next save writes over it (hero grids,
    /// seen on the stand 26.10.2026 with Steam Cloud off).
    /// </summary>
    /// <param name="relativePath">A path under remote, with forward slashes.</param>
    public static bool SteamLists(string gameFolder, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        const string Remote = "remote/";
        if (!relativePath.StartsWith(Remote, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{relativePath} is not a remote file", nameof(relativePath));
        }

        string index = Path.Combine(gameFolder, "remotecache.vdf");
        try
        {
            return File.Exists(index) && File.ReadAllText(index).Contains($"\"{relativePath[Remote.Length..]}\"", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Steam is writing its index right now: the write is not held back over a moment's lock.
            return true;
        }
    }

    private static JsonObject ParseGrids(byte[] json)
    {
        try
        {
            ReadOnlySpan<byte> text = json.AsSpan();
            if (text.StartsWith(Encoding.UTF8.Preamble))
            {
                text = text[Encoding.UTF8.Preamble.Length..];
            }

            if (JsonNode.Parse(text) is JsonObject root && root["configs"] is JsonArray)
            {
                return root;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{HeroGridPath} is not valid JSON: {ex.Message}", ex);
        }

        throw new InvalidDataException($"{HeroGridPath} has no \"configs\" list");
    }

    private static string NameOf(JsonNode? config) =>
        config is JsonObject grid && grid["config_name"] is JsonValue value && value.TryGetValue(out string? name) ? name : string.Empty;
}

/// <summary>transfer.json next to the settings and the copies in transfer\&lt;id&gt;.</summary>
public sealed class TransferBackupStore(string file, string backupRoot, FileLog log)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string BackupFolder(uint accountId) => Path.Combine(backupRoot, accountId.ToString(CultureInfo.InvariantCulture));

    public IReadOnlyList<TransferBackup> Load()
    {
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file, Utf8NoBom), TransferJsonContext.Default.ListTransferBackup) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Error($"Transfer state {file} is unreadable", ex);
            throw new InvalidDataException($"{file} is unreadable: {ex.Message}", ex);
        }
    }

    public void Save(IReadOnlyList<TransferBackup> backups)
    {
        ArgumentNullException.ThrowIfNull(backups);
        if (backups.Count == 0)
        {
            File.Delete(file);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize([.. backups], TransferJsonContext.Default.ListTransferBackup), Utf8NoBom);
        File.Move(temp, file, overwrite: true);
    }
}

/// <summary>Carries game settings between the accounts of this computer and can put the target back.</summary>
public sealed class TransferService(TransferBackupStore store, FileLog log)
{
    /// <summary>Accounts with userdata\&lt;id&gt;\570, names from loginusers.vdf.</summary>
    public static IReadOnlyList<GameAccount> Accounts(GameState game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Steam is not { } steam)
        {
            return [];
        }

        IReadOnlyDictionary<uint, string> names = SteamLocator.AccountNames(steam.Root);
        var accounts = new List<GameAccount>();
        foreach (string folder in ConvarFiles.GameFolders(steam.Root))
        {
            string idText = Path.GetFileName(Path.GetDirectoryName(folder)!);
            uint id = uint.Parse(idText, NumberStyles.None, CultureInfo.InvariantCulture);
            if (id != 0)
            {
                accounts.Add(new GameAccount(id, names.GetValueOrDefault(id, string.Empty), CloudEnabled(steam.Root, id)));
            }
        }

        return accounts;
    }

    /// <summary>
    /// Steam Cloud for Dota, from userdata\&lt;id&gt;\7\remote\sharedconfig.vdf: both the
    /// global switch and the one of the game. A missing or unreadable file counts as on, as in Steam.
    /// </summary>
    public static bool CloudEnabled(string steamRoot, uint accountId)
    {
        string file = Path.Combine(steamRoot, "userdata", accountId.ToString(CultureInfo.InvariantCulture), "7", "remote", "sharedconfig.vdf");
        if (!File.Exists(file))
        {
            return true;
        }

        try
        {
            ValveKeyValue.KVObject? steam = KeyValues.Load(file).Path("Software", "Valve", "Steam");
            return steam is null
                || (steam.Child("cloudenabled").Text() != "0" && steam.Path("apps", LaunchOptions.DotaAppId, "cloudenabled").Text() != "0");
        }
        catch (Exception ex) when (ex is KeyValuesException or IOException or UnauthorizedAccessException)
        {
            // Only decides whether the cloud hint is shown; showing it once too often is harmless.
            return true;
        }
    }

    public TransferBackup? BackupOf(uint accountId) => store.Load().FirstOrDefault(b => b.AccountId == accountId);

    /// <summary>Accounts that can be put back.</summary>
    public IReadOnlySet<uint> Restorable() => store.Load().Select(b => b.AccountId).ToHashSet();

    /// <summary>Copies the chosen groups; whatever is replaced is copied first.</summary>
    /// <exception cref="InvalidDataException">A hero grid file is broken, or transfer.json is unreadable.</exception>
    public TransferResult Apply(GameState game, uint source, uint target, IReadOnlyCollection<TransferGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(groups);
        if (game.DotaRunning)
        {
            return new TransferResult(TransferRefusal.DotaRunning, 0);
        }

        if (game.Steam is not { } steam)
        {
            return new TransferResult(TransferRefusal.NoSteam, 0);
        }

        if (source == target)
        {
            return new TransferResult(TransferRefusal.SameAccount, 0);
        }

        string from = TransferFiles.GameFolder(steam.Root, source);
        string into = TransferFiles.GameFolder(steam.Root, target);
        if (!Directory.Exists(from) || !Directory.Exists(into))
        {
            return new TransferResult(TransferRefusal.NoSuchAccount, 0);
        }

        bool launch = groups.Contains(TransferGroup.LaunchOptions);
        if (launch && game.SteamRunning)
        {
            return new TransferResult(TransferRefusal.SteamRunning, 0);
        }

        (IReadOnlyList<(string Path, byte[] Content)> files, bool gridsSkipped) = WithoutUnseenGrids(into, TransferFiles.Plan(from, into, groups));
        string? line = launch ? LaunchOptions.Read(steam.Root, source) : null;
        string? targetLine = line is null ? null : LaunchOptions.Read(steam.Root, target);
        if (targetLine is null || string.Equals(line, targetLine, StringComparison.Ordinal))
        {
            line = null;
        }

        if (files.Count == 0 && line is null)
        {
            return new TransferResult(gridsSkipped ? TransferRefusal.GridsNotSeen : TransferRefusal.NothingToCopy, 0, gridsSkipped);
        }

        int written = WriteWithBackup(steam.Root, target, files, line, targetLine);
        log.Info($"Transfer {source} -> {target}: {string.Join(", ", groups)}; {written} written{(gridsSkipped ? ", hero grids skipped" : string.Empty)}");
        return new TransferResult(null, written, gridsSkipped);
    }

    /// <summary>Writes files from elsewhere (a preset) into the account, with the same copies and "put back".</summary>
    public TransferResult ApplyFiles(GameState game, uint target, IReadOnlyList<(string Path, byte[] Content)> files)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(files);
        if (game.DotaRunning)
        {
            return new TransferResult(TransferRefusal.DotaRunning, 0);
        }

        if (game.Steam is not { } steam)
        {
            return new TransferResult(TransferRefusal.NoSteam, 0);
        }

        string into = TransferFiles.GameFolder(steam.Root, target);
        if (!Directory.Exists(into))
        {
            return new TransferResult(TransferRefusal.NoSuchAccount, 0);
        }

        (IReadOnlyList<(string Path, byte[] Content)> seen, bool gridsSkipped) = WithoutUnseenGrids(into, files);
        if (seen.Count == 0)
        {
            return new TransferResult(gridsSkipped ? TransferRefusal.GridsNotSeen : TransferRefusal.NothingToCopy, 0, gridsSkipped);
        }

        int written = WriteWithBackup(steam.Root, target, seen, null, null);
        log.Info($"Preset files into {target}: {string.Join(", ", seen.Select(f => f.Path))}{(gridsSkipped ? "; hero grids skipped" : string.Empty)}");
        return new TransferResult(null, written, gridsSkipped);
    }

    /// <summary>The files without hero grids when the game of the account would not read them.</summary>
    private static (IReadOnlyList<(string Path, byte[] Content)> Files, bool GridsSkipped) WithoutUnseenGrids(
        string into, IReadOnlyList<(string Path, byte[] Content)> files)
    {
        bool grids = files.Any(f => f.Path == TransferFiles.HeroGridPath);
        return grids && !TransferFiles.SteamLists(into, TransferFiles.HeroGridPath)
            ? ([.. files.Where(f => f.Path != TransferFiles.HeroGridPath)], true)
            : (files, false);
    }

    /// <summary>
    /// Copies what will be replaced, saves the record, then writes. The copies and their record
    /// go first: a file replaced without them could never be put back.
    /// </summary>
    private int WriteWithBackup(string steamRoot, uint target, IReadOnlyList<(string Path, byte[] Content)> files, string? line, string? targetLine)
    {
        string into = TransferFiles.GameFolder(steamRoot, target);
        string targetConfig = LaunchOptions.LocalConfigPath(steamRoot, target);
        List<TransferBackup> backups = [.. store.Load()];
        TransferBackup? earlier = backups.FirstOrDefault(b => b.AccountId == target);
        string folder = store.BackupFolder(target);
        if (earlier is null && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        List<TransferEntry> entries = [.. earlier?.Files ?? []];
        foreach ((string path, byte[] content) in files)
        {
            string sha1 = PackBuilder.Sha1(content);
            int index = entries.FindIndex(e => string.Equals(e.RelativePath, path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                // The copy from before the first transfer stays; the file now holds this transfer's content.
                entries[index] = entries[index] with { Written = sha1 };
                continue;
            }

            string current = TransferFiles.Full(into, path);
            string? copy = null;
            if (File.Exists(current))
            {
                Directory.CreateDirectory(folder);
                copy = string.Create(CultureInfo.InvariantCulture, $"{entries.Count}.bak");
                File.Copy(current, Path.Combine(folder, copy), overwrite: true);
            }

            entries.Add(new TransferEntry(path, copy, sha1));
        }

        bool launchTouched = earlier?.LaunchTouched == true || line is not null;
        string? launchBefore = earlier?.LaunchTouched == true ? earlier.LaunchBefore : targetLine;
        string? launchWritten = line ?? earlier?.LaunchWritten;
        backups.RemoveAll(b => b.AccountId == target);
        backups.Add(new TransferBackup(
            target, entries, launchTouched, launchTouched ? launchBefore : null, earlier?.At ?? DateTimeOffset.Now, launchTouched ? launchWritten : null));
        store.Save(backups);

        foreach ((string path, byte[] content) in files)
        {
            WriteFile(TransferFiles.Full(into, path), content);
        }

        int written = files.Count;
        if (line is not null && LocalConfigEditor.SetLaunchOptions(targetConfig, line))
        {
            written++;
        }

        return written;
    }

    /// <summary>
    /// Puts the account back as it was before the first transfer into it. A file or the launch line
    /// changed since the transfer, by the player, the game or Steam, stays as it is: putting it back
    /// would lose that change. The record is forgotten either way, as with the launch options.
    /// </summary>
    /// <exception cref="InvalidDataException">transfer.json is unreadable.</exception>
    /// <exception cref="KeyValuesException">localconfig.vdf is unreadable.</exception>
    public TransferResult Restore(GameState game, uint target)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.DotaRunning)
        {
            return new TransferResult(TransferRefusal.DotaRunning, 0);
        }

        if (game.Steam is not { } steam)
        {
            return new TransferResult(TransferRefusal.NoSteam, 0);
        }

        List<TransferBackup> backups = [.. store.Load()];
        if (backups.FirstOrDefault(b => b.AccountId == target) is not { } backup)
        {
            return new TransferResult(TransferRefusal.NothingToCopy, 0);
        }

        if (backup.LaunchTouched && game.SteamRunning)
        {
            return new TransferResult(TransferRefusal.SteamRunning, 0);
        }

        string into = TransferFiles.GameFolder(steam.Root, target);
        string folder = store.BackupFolder(target);
        int restored = 0;
        var kept = new List<string>();
        foreach (TransferEntry entry in backup.Files)
        {
            string file = TransferFiles.Full(into, entry.RelativePath);
            byte[]? now = File.Exists(file) ? File.ReadAllBytes(file) : null;
            byte[]? before = entry.Backup is null ? null : File.ReadAllBytes(Path.Combine(folder, entry.Backup));
            if (now is null ? before is null : before is not null && now.AsSpan().SequenceEqual(before))
            {
                // Already as before: created by the transfer and gone since, or put back by hand or by a put back cut short.
                continue;
            }

            if (entry.Written is not null && (now is null || PackBuilder.Sha1(now) != entry.Written))
            {
                kept.Add(entry.RelativePath);
                continue;
            }

            if (before is null)
            {
                // The file was created by the transfer.
                File.Delete(file);
            }
            else
            {
                WriteFile(file, before);
            }

            restored++;
        }

        string config = LaunchOptions.LocalConfigPath(steam.Root, target);
        if (backup.LaunchTouched && File.Exists(config))
        {
            string? now = LaunchOptions.ReadFile(config);
            string before = backup.LaunchBefore ?? string.Empty;

            // A line already as before needs nothing, also when the transfer could not write it: the account had no Dota block yet.
            if (!string.Equals(now, before, StringComparison.Ordinal))
            {
                if (backup.LaunchWritten is not null && !string.Equals(now, backup.LaunchWritten, StringComparison.Ordinal))
                {
                    kept.Add("launch options");
                }
                else if (LocalConfigEditor.SetLaunchOptions(config, before))
                {
                    restored++;
                }
            }
        }

        backups.Remove(backup);
        store.Save(backups);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        log.Info($"Transfer into {target} put back: {restored} restored{(kept.Count > 0 ? $", changed since and left as they are: {string.Join(", ", kept)}" : string.Empty)}");
        return new TransferResult(kept.Count > 0 ? TransferRefusal.ChangedSince : null, restored);
    }

    private static void WriteFile(string file, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".dotatune.tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, file, overwrite: true);
    }
}
