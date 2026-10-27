using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotaTune.Core.Config;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Game;
using DotaTune.Core.Steam;

namespace DotaTune.Core.Presets;

/// <summary>A part of a settings copy; a restore takes each one or leaves it. In the order the page shows them.</summary>
public enum BackupPart
{
    Graphics,
    Hotkeys,
    ConsoleBinds,
    GameSettings,
    LaunchOptions,
    Autoexec,
    HeroGrids,
    Builds,
    Misc,
}

/// <summary>manifest.json of a settings copy.</summary>
/// <param name="Kind">Always <see cref="SettingsBackupFile.Kind"/>: a preset has a manifest.json too.</param>
/// <param name="Account">Login of the account the copy was taken from, to show; null when unknown.</param>
/// <param name="Machine">Short fingerprint of the computer name: graphics of another computer are not taken by default.</param>
/// <param name="LaunchOptions">The Dota launch line; null when the copy has none.</param>
/// <param name="Missing">Entries that did not exist, so a restore deletes them; only in the copy taken before a restore.</param>
public sealed record BackupManifest(
    int FormatVersion,
    string Kind,
    string AppVersion,
    string? GameBuild,
    DateTimeOffset CreatedAt,
    string? Account,
    string? Machine,
    string? LaunchOptions,
    IReadOnlyList<string>? Missing = null);

/// <summary>
/// The account's own settings: files of userdata\&lt;id&gt;\570 by their path there, the own lines
/// of autoexec.cfg and the launch line in the manifest.
/// </summary>
public sealed record SettingsBackup(BackupManifest Manifest, IReadOnlyDictionary<string, byte[]> Files, byte[]? Autoexec)
{
    /// <summary>The parts the copy holds, in the order of <see cref="BackupPart"/>.</summary>
    public IReadOnlyList<BackupPart> Parts =>
    [
        .. Enum.GetValues<BackupPart>().Where(part => part switch
        {
            BackupPart.LaunchOptions => Manifest.LaunchOptions is not null,
            BackupPart.Autoexec => Autoexec is not null || Manifest.Missing?.Contains(SettingsBackupFile.AutoexecName) == true,
            _ => Files.Keys.Concat(Manifest.Missing ?? []).Any(path => SettingsBackupFile.PartOf(path) == part),
        }),
    ];

    /// <summary>The copy was taken on this computer, so its graphics fit the screen here.</summary>
    public bool FromThisComputer => string.Equals(Manifest.Machine, SettingsBackupFile.ThisMachine, StringComparison.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(BackupManifest))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;

/// <summary>
/// A settings copy on disk: a plain zip a person can open without DotaTune. Inside are
/// manifest.json, autoexec.cfg and the account files under 570/ with the paths Steam uses.
/// Reading takes only the files the transfer groups know, so nothing lands outside them.
/// </summary>
public static class SettingsBackupFile
{
    public const int FormatVersion = 1;
    public const string Kind = "dotatune-settings";
    public const string Extension = ".zip";
    public const string AutoexecName = "autoexec.cfg";
    public const long MaxFileBytes = 32 * 1024 * 1024;
    public const int MaxEntryBytes = 4 * 1024 * 1024;
    public const int MaxEntries = 4096;
    private const string ManifestName = "manifest.json";
    private const string GamePrefix = "570/";

    /// <summary>First 16 hex digits of SHA-256 of the computer name; the name itself stays out of the file.</summary>
    public static string ThisMachine { get; } =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName.ToUpperInvariant())))[..16];

    /// <summary>The part an entry belongs to: a file of the game folder or autoexec.cfg; null for anything else.</summary>
    public static BackupPart? PartOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path == AutoexecName)
        {
            return BackupPart.Autoexec;
        }

        return TransferFiles.GroupOf(path) switch
        {
            TransferGroup.Hotkeys => BackupPart.Hotkeys,
            TransferGroup.GameSettings => BackupPart.GameSettings,
            TransferGroup.ConsoleBinds => BackupPart.ConsoleBinds,
            TransferGroup.HeroGrids => BackupPart.HeroGrids,
            TransferGroup.Builds => BackupPart.Builds,
            TransferGroup.Misc => BackupPart.Misc,
            TransferGroup.Graphics => BackupPart.Graphics,
            _ => null,
        };
    }

    public static void Write(string path, SettingsBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        string temp = path + ".tmp";
        try
        {
            using (var zip = new ZipArchive(File.Create(temp), ZipArchiveMode.Create))
            {
                Add(ManifestName, JsonSerializer.SerializeToUtf8Bytes(backup.Manifest, BackupJsonContext.Default.BackupManifest));
                if (backup.Autoexec is not null)
                {
                    Add(AutoexecName, backup.Autoexec);
                }

                foreach ((string file, byte[] content) in backup.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
                {
                    Add(GamePrefix + file, content);
                }

                void Add(string name, byte[] content)
                {
                    using Stream entry = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                    entry.Write(content);
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // A half-written copy is not left next to the one the person chose.
            File.Delete(temp);
            throw;
        }
    }

    /// <exception cref="PresetException">Not a settings copy, a newer format, broken or too large.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static SettingsBackup Read(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > MaxFileBytes)
        {
            throw new PresetException(PresetProblem.TooLarge, $"The file is larger than {MaxFileBytes} bytes");
        }

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new PresetException(PresetProblem.NotAPreset, "The file is not a zip archive", ex);
        }

        using (zip)
        {
            if (zip.Entries.Count > MaxEntries)
            {
                throw new PresetException(PresetProblem.TooLarge, $"The copy has more than {MaxEntries} entries");
            }

            byte[]? manifestBytes = null;
            byte[]? autoexec = null;
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName;
                string? file = name.StartsWith(GamePrefix, StringComparison.Ordinal) ? name[GamePrefix.Length..] : null;

                // Folders and anything the groups do not know are left in the zip.
                bool wanted = name == ManifestName || name == AutoexecName || (file is not null && TransferFiles.GroupOf(file) is not null);
                if (!wanted)
                {
                    continue;
                }

                byte[] content;
                try
                {
                    using Stream data = entry.Open();
                    content = Limits.ReadAll(data, MaxEntryBytes);
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                {
                    // NotSupported: a password or a compression method other than Deflate, not made by DotaTune.
                    throw new PresetException(PresetProblem.Damaged, $"Entry \"{name}\" does not unpack", ex);
                }

                if (name == ManifestName ? manifestBytes is not null : name == AutoexecName ? autoexec is not null : files.ContainsKey(file!))
                {
                    throw new PresetException(PresetProblem.Damaged, $"Entry \"{name}\" is there twice");
                }

                if (name == ManifestName)
                {
                    manifestBytes = content;
                }
                else if (name == AutoexecName)
                {
                    autoexec = content;
                }
                else
                {
                    files[file!] = content;
                }
            }

            BackupManifest manifest = ParseManifest(manifestBytes);

            // A copy holds own lines only: a DotaTune mark among them would double the block on restore.
            if (autoexec is not null && HasMarks(autoexec))
            {
                throw new PresetException(PresetProblem.Damaged, "autoexec.cfg of the copy has DotaTune marks");
            }

            return new SettingsBackup(
                manifest with { Missing = manifest.Missing?.Where(m => PartOf(m) is not null).Distinct(StringComparer.Ordinal).ToList() },
                files,
                autoexec);
        }
    }

    private static bool HasMarks(byte[] autoexec)
    {
        try
        {
            return AutoexecText.FindBlock(AutoexecText.Encoding.GetString(autoexec)) is not null;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static BackupManifest ParseManifest(byte[]? bytes)
    {
        if (bytes is null)
        {
            throw new PresetException(PresetProblem.NotAPreset, "There is no manifest.json");
        }

        BackupManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(bytes, BackupJsonContext.Default.BackupManifest);
        }
        catch (JsonException ex)
        {
            throw new PresetException(PresetProblem.Damaged, "manifest.json is not valid", ex);
        }

        // A preset or another program's zip with a manifest of its own.
        if (manifest is null || !string.Equals(manifest.Kind, Kind, StringComparison.Ordinal) || manifest.FormatVersion < 1)
        {
            throw new PresetException(PresetProblem.NotAPreset, "manifest.json is not of a settings copy");
        }

        if (manifest.FormatVersion > FormatVersion)
        {
            throw new PresetException(PresetProblem.Newer, $"Copy format {manifest.FormatVersion} is newer than {FormatVersion}");
        }

        return manifest;
    }
}

public enum BackupRefusal
{
    DotaRunning,

    /// <summary>The launch line differs, and Steam rewrites it from memory on exit.</summary>
    SteamRunning,
    NoAccount,

    /// <summary>Dota was never started on this account here: the account has no userdata\&lt;id&gt;\570.</summary>
    NoGameFolder,
    NothingChosen,

    /// <summary>A DotaTune mark in autoexec.cfg is missing or doubled.</summary>
    BrokenMarks,

    /// <summary>Undo was asked for, and there is no copy from before a restore.</summary>
    NothingToUndo,
}

/// <summary>Refusal, or how many files and lines were changed.</summary>
/// <param name="GridsSkipped">Hero grids were left out: Steam does not list the account's file, see <see cref="TransferFiles.SteamLists"/>.</param>
/// <param name="LaunchSkipped">The launch line was left out: the account has no localconfig.vdf or no Dota block in it.</param>
public sealed record BackupResult(BackupRefusal? Refusal, int Written, bool GridsSkipped = false, bool LaunchSkipped = false);

/// <summary>
/// Takes a copy of the signed-in account's own settings and puts one back. A restore first saves
/// what it replaces to <paramref name="undoFile"/>; the transfer and reset records stay out of it,
/// so a later reset does not take a person's own settings back.
/// </summary>
public sealed class SettingsBackupService(string undoFile, AutoexecService autoexec, FileLog log)
{
    /// <summary>Every group of files except the launch line, which goes into the manifest.</summary>
    private static readonly TransferGroup[] FileGroups = [.. TransferFiles.All.Where(g => g != TransferGroup.LaunchOptions)];

    /// <summary>The copy of the signed-in account; null without one.</summary>
    /// <param name="appVersion">The version written into the manifest.</param>
    /// <exception cref="InvalidDataException">A DotaTune mark in autoexec.cfg is missing or doubled.</exception>
    public SettingsBackup? Take(GameState game, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Steam is not { Account: { } account } steam)
        {
            return null;
        }

        string folder = TransferFiles.GameFolder(steam.Root, account.AccountId);
        IReadOnlyList<(string Path, byte[] Content)> files = Directory.Exists(folder) ? TransferFiles.Collect(folder, FileGroups) : [];
        var manifest = new BackupManifest(
            SettingsBackupFile.FormatVersion, SettingsBackupFile.Kind, appVersion, game.Dota?.Build, DateTimeOffset.Now,
            account.AccountName.Length == 0 ? null : account.AccountName, SettingsBackupFile.ThisMachine, game.LaunchOptions);
        return new SettingsBackup(manifest, files.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal), autoexec.OwnText(game));
    }

    /// <summary>A running Steam has to close: the launch line is chosen and differs from the account's.</summary>
    public static bool NeedsSteamClosed(GameState game, SettingsBackup backup, IReadOnlyCollection<BackupPart> parts)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(parts);
        return parts.Contains(BackupPart.LaunchOptions)
            && backup.Manifest.LaunchOptions is { } line
            && game.LaunchOptions is { } now
            && !string.Equals(line, now, StringComparison.Ordinal);
    }

    /// <summary>Puts the chosen parts back into the signed-in account; what they replace is saved for <see cref="Undo"/>.</summary>
    /// <exception cref="KeyValuesException">The account's localconfig.vdf is unreadable.</exception>
    public BackupResult Restore(GameState game, SettingsBackup backup, IReadOnlyCollection<BackupPart> parts) =>
        Put(game, backup, parts, keepUndo: true);

    /// <summary>Puts back what the last restore replaced, and forgets it.</summary>
    /// <exception cref="InvalidDataException">The saved copy is broken.</exception>
    /// <exception cref="KeyValuesException">The account's localconfig.vdf is unreadable.</exception>
    public BackupResult Undo(GameState game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (ReadUndo() is not { } before)
        {
            return new BackupResult(BackupRefusal.NothingToUndo, 0);
        }

        BackupResult result = Put(game, before, before.Parts, keepUndo: false);
        if (result.Refusal is null)
        {
            File.Delete(undoFile);
        }

        return result;
    }

    /// <summary>Drops the copy from before a restore of an earlier session: undo is offered only in the session of the restore.</summary>
    public void ForgetUndo()
    {
        try
        {
            File.Delete(undoFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next start; the next restore writes over it anyway.
            log.Error($"The copy from before a restore {undoFile} was not deleted", ex);
        }
    }

    /// <summary>A running Steam has to close for <see cref="Undo"/>: the restore changed the launch line.</summary>
    /// <exception cref="InvalidDataException">The saved copy is broken.</exception>
    public bool UndoNeedsSteamClosed(GameState game) =>
        ReadUndo() is { } before && NeedsSteamClosed(game, before, before.Parts);

    private SettingsBackup? ReadUndo()
    {
        if (!File.Exists(undoFile))
        {
            return null;
        }

        try
        {
            return SettingsBackupFile.Read(undoFile);
        }
        catch (PresetException ex)
        {
            log.Error($"The copy from before a restore {undoFile} is broken", ex);
            throw new InvalidDataException($"{undoFile} is broken: {ex.Message}", ex);
        }
    }

    private BackupResult Put(GameState game, SettingsBackup backup, IReadOnlyCollection<BackupPart> parts, bool keepUndo)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(parts);
        if (game.DotaRunning)
        {
            return new BackupResult(BackupRefusal.DotaRunning, 0);
        }

        if (game.Steam is not { Account: { } account } steam)
        {
            return new BackupResult(BackupRefusal.NoAccount, 0);
        }

        HashSet<BackupPart> chosen = [.. parts.Intersect(backup.Parts)];
        if (chosen.Count == 0)
        {
            return new BackupResult(BackupRefusal.NothingChosen, 0);
        }

        if (NeedsSteamClosed(game, backup, chosen) && game.SteamRunning)
        {
            return new BackupResult(BackupRefusal.SteamRunning, 0);
        }

        string folder = TransferFiles.GameFolder(steam.Root, account.AccountId);
        IReadOnlyList<string> missing = backup.Manifest.Missing ?? [];
        Dictionary<string, byte[]?> files = [];
        foreach ((string path, byte[] content) in backup.Files.Where(f => Chosen(f.Key)))
        {
            files[path] = content;
        }

        foreach (string path in missing.Where(m => m != SettingsBackupFile.AutoexecName && Chosen(m)))
        {
            files.TryAdd(path, null);
        }

        if (files.Count > 0 && !Directory.Exists(folder))
        {
            return new BackupResult(BackupRefusal.NoGameFolder, 0);
        }

        bool gridsSkipped = false;
        if (files.TryGetValue(TransferFiles.HeroGridPath, out byte[]? grids) && grids is not null && !TransferFiles.SteamLists(folder, TransferFiles.HeroGridPath))
        {
            files.Remove(TransferFiles.HeroGridPath);
            gridsSkipped = true;
        }

        // Only what differs is written, so a restore right after a copy changes nothing.
        var current = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (string path in files.Keys.ToList())
        {
            string file = TransferFiles.Full(folder, path);
            byte[]? now = File.Exists(file) ? File.ReadAllBytes(file) : null;
            if (now is null ? files[path] is null : files[path] is { } wanted && now.AsSpan().SequenceEqual(wanted))
            {
                files.Remove(path);
            }
            else
            {
                current[path] = now;
            }
        }

        bool takeAutoexec = chosen.Contains(BackupPart.Autoexec) && game.Dota is not null;
        byte[]? ownNow = null;
        if (takeAutoexec)
        {
            // Only broken marks are a refusal; an unreadable autoexec.json goes up as the error it is.
            try
            {
                ownNow = autoexec.OwnText(game);
            }
            catch (InvalidDataException) when (autoexec.MarksBroken(game))
            {
                return new BackupResult(BackupRefusal.BrokenMarks, 0);
            }

            takeAutoexec = !(ownNow is null ? backup.Autoexec is null : backup.Autoexec is not null && ownNow.AsSpan().SequenceEqual(backup.Autoexec));
        }

        string? line = chosen.Contains(BackupPart.LaunchOptions) ? backup.Manifest.LaunchOptions : null;
        string? lineNow = line is null ? null : LaunchOptions.Read(steam.Root, account.AccountId);
        bool launchSkipped = line is not null && lineNow is null;
        if (lineNow is null || string.Equals(line, lineNow, StringComparison.Ordinal))
        {
            line = null;
        }

        if (files.Count == 0 && !takeAutoexec && line is null)
        {
            return new BackupResult(null, 0, gridsSkipped, launchSkipped);
        }

        if (keepUndo)
        {
            SaveUndo(game, current, takeAutoexec, ownNow, line is null ? null : lineNow, backup.Manifest.Account);
        }

        foreach ((string path, byte[]? content) in files)
        {
            string file = TransferFiles.Full(folder, path);
            if (content is null)
            {
                File.Delete(file);
            }
            else
            {
                TransferService.WriteFile(file, content);
            }
        }

        int written = files.Count;
        if (takeAutoexec && autoexec.ReplaceOwnText(game, backup.Autoexec) is null)
        {
            written++;
        }

        if (line is not null)
        {
            if (LocalConfigEditor.SetLaunchOptions(LaunchOptions.LocalConfigPath(steam.Root, account.AccountId), line))
            {
                written++;
            }
            else
            {
                launchSkipped = true;
            }
        }

        log.Info($"Settings copy of {backup.Manifest.Account ?? "an account"} from {backup.Manifest.CreatedAt:O} put into {account.AccountId}: "
            + $"{string.Join(", ", chosen)}; {written} written{(gridsSkipped ? ", hero grids skipped" : string.Empty)}{(launchSkipped ? ", no Dota launch block" : string.Empty)}");
        return new BackupResult(null, written, gridsSkipped, launchSkipped);

        bool Chosen(string path) => SettingsBackupFile.PartOf(path) is BackupPart part && chosen.Contains(part);
    }

    /// <summary>What the restore replaces, as a copy of its own; a file the restore creates is listed as missing.</summary>
    /// <param name="current">The files the restore changes, with what they hold now; null for a file that is not there.</param>
    private void SaveUndo(GameState game, IReadOnlyDictionary<string, byte[]?> current, bool autoexecChanges, byte[]? ownNow, string? lineNow, string? account)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach ((string path, byte[]? now) in current)
        {
            if (now is null)
            {
                missing.Add(path);
            }
            else
            {
                files[path] = now;
            }
        }

        if (autoexecChanges && ownNow is null)
        {
            missing.Add(SettingsBackupFile.AutoexecName);
        }

        var manifest = new BackupManifest(
            SettingsBackupFile.FormatVersion, SettingsBackupFile.Kind, string.Empty, game.Dota?.Build, DateTimeOffset.Now,
            account, SettingsBackupFile.ThisMachine, lineNow, missing);
        Directory.CreateDirectory(Path.GetDirectoryName(undoFile)!);
        SettingsBackupFile.Write(undoFile, new SettingsBackup(manifest, files, autoexecChanges ? ownNow : null));
    }
}
