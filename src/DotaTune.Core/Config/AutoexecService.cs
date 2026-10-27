using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Game;

namespace DotaTune.Core.Config;

public enum ConfigRefusal
{
    DotaRunning,
    NoGame,

    /// <summary>A DotaTune mark in autoexec.cfg is missing or doubled: the file was edited by hand.</summary>
    BrokenMarks,
}

public enum AutoexecHealth
{
    NotWritten,
    Ready,

    /// <summary>The block or the whole file is gone.</summary>
    Missing,

    /// <summary>The lines inside the block were edited by hand.</summary>
    Changed,
}

/// <summary>What a convar file held before DotaTune wrote autoexec; null when it had no such line.</summary>
public sealed record SavedValue(string File, string Name, string? Value);

/// <param name="CreatedFile">There was no autoexec.cfg, so an empty one is deleted again.</param>
/// <param name="AddedLineBreak">The last line of the file had no line break and DotaTune added one before its block.</param>
public sealed record AutoexecState(
    string AutoexecPath,
    bool CreatedFile,
    bool AddedLineBreak,
    IReadOnlyList<ConfigLine> Lines,
    IReadOnlyList<SavedValue> Previous,
    DateTimeOffset WrittenAt);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AutoexecState))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>autoexec.json next to the settings: the only record of what DotaTune wrote and what it replaced.</summary>
public sealed class AutoexecStateStore(string file, FileLog log)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public AutoexecState? Load()
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file, Utf8NoBom), ConfigJsonContext.Default.AutoexecState);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Error($"Autoexec state {file} is unreadable", ex);
            throw new InvalidDataException($"{file} is unreadable: {ex.Message}", ex);
        }
    }

    public void Save(AutoexecState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, ConfigJsonContext.Default.AutoexecState), Utf8NoBom);
        File.Move(temp, file, overwrite: true);
    }

    public void Clear() => File.Delete(file);
}

/// <summary>
/// Writes the chosen lines into game\dota\cfg\autoexec.cfg, which the game runs at every start,
/// and takes them out again. The person's own lines are never touched. autoexec is shared by
/// every Steam account of the computer, so the values the game saves from it are remembered
/// and returned for every account.
/// </summary>
public sealed class AutoexecService(AutoexecStateStore store, FileLog log)
{
    public AutoexecState? Current => store.Load();

    public static string PathIn(string gameRoot) => Path.Combine(gameRoot, "game", "dota", "cfg", "autoexec.cfg");

    /// <summary>Puts the lines in place of the earlier ones; no lines only takes the earlier ones out. Null when done.</summary>
    public ConfigRefusal? Apply(GameState game, IReadOnlyList<ConfigLine> lines)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(lines);
        if (game.DotaRunning)
        {
            return ConfigRefusal.DotaRunning;
        }

        if (game.Dota is null || game.Steam is null)
        {
            return ConfigRefusal.NoGame;
        }

        // A line that cannot go into the file throws here, before the earlier block is taken out.
        _ = AutoexecText.WithBlock(string.Empty, lines);
        string path = PathIn(game.Dota.GameRoot);
        if (!MarksAreSound(ReadText(path)))
        {
            return ConfigRefusal.BrokenMarks;
        }

        if (Remove(game) is ConfigRefusal refusal)
        {
            return refusal;
        }

        if (lines.Count == 0)
        {
            return null;
        }

        string? before = ReadText(path);
        string text = before ?? string.Empty;
        var previous = new List<SavedValue>();
        foreach (string file in ConvarFiles.GameFolders(game.Steam.Root).SelectMany(ConvarFiles.FilesIn))
        {
            IReadOnlyDictionary<string, string?> values = ConvarFiles.Read(file, lines.Select(l => l.Name));
            previous.AddRange(lines.Select(line => new SavedValue(file, line.Name, values[line.Name])));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteText(path, AutoexecText.WithBlock(text, lines));
        var state = new AutoexecState(path, before is null, text.Length > 0 && !text.EndsWith('\n'), lines, previous, DateTimeOffset.Now);
        try
        {
            store.Save(state);
        }
        catch (Exception ex)
        {
            // Without a saved state nobody would ever take the lines out: put the file back.
            log.Error($"Autoexec state was not saved, {path} is put back", ex);
            if (before is null)
            {
                File.Delete(path);
            }
            else
            {
                WriteText(path, before);
            }

            throw;
        }

        log.Info($"Autoexec written: {path}, {string.Join("; ", lines)}, {previous.Count(p => p.Value is not null)} saved values remembered");
        return null;
    }

    /// <summary>
    /// Takes the block out and returns every saved value that still holds what DotaTune wrote;
    /// a value changed since in the game stays. Null when done or when nothing was written.
    /// </summary>
    public ConfigRefusal? Remove(GameState game)
    {
        ArgumentNullException.ThrowIfNull(game);
        AutoexecState? state = store.Load();
        if (state is null)
        {
            return null;
        }

        if (game.DotaRunning)
        {
            return ConfigRefusal.DotaRunning;
        }

        string? text = ReadText(state.AutoexecPath);
        if (!MarksAreSound(text))
        {
            return ConfigRefusal.BrokenMarks;
        }

        if (text is not null)
        {
            string rest = AutoexecText.WithoutBlock(text, state.AddedLineBreak);
            if (state.CreatedFile && rest.Trim().Length == 0)
            {
                File.Delete(state.AutoexecPath);
            }
            else if (!string.Equals(rest, text, StringComparison.Ordinal))
            {
                WriteText(state.AutoexecPath, rest);
            }
        }

        foreach (SavedValue saved in state.Previous)
        {
            ConfigLine? written = state.Lines.FirstOrDefault(l => string.Equals(l.Name, saved.Name, StringComparison.OrdinalIgnoreCase));
            string? now = File.Exists(saved.File) ? ConvarFiles.Read(saved.File, saved.Name) : null;
            if (written is null || now is null || !ConvarFiles.SameValue(now, written.Value))
            {
                continue;
            }

            ConvarFiles.Write(saved.File, saved.Name, saved.Value);
            log.Info($"{saved.Name} in {saved.File}: {now} -> {saved.Value ?? "line removed"}");
        }

        store.Clear();
        log.Info($"Autoexec lines removed from {state.AutoexecPath}");
        return null;
    }

    public AutoexecHealth Check()
    {
        AutoexecState? state = store.Load();
        if (state is null)
        {
            return AutoexecHealth.NotWritten;
        }

        string? text = ReadText(state.AutoexecPath);
        if (text is null)
        {
            return AutoexecHealth.Missing;
        }

        IReadOnlyList<ConfigLine>? lines;
        try
        {
            lines = AutoexecText.BlockLines(text);
        }
        catch (InvalidDataException)
        {
            return AutoexecHealth.Changed;
        }

        return lines is null ? AutoexecHealth.Missing
            : lines.SequenceEqual(state.Lines) ? AutoexecHealth.Ready
            : AutoexecHealth.Changed;
    }

    private static bool MarksAreSound(string? text)
    {
        try
        {
            AutoexecText.FindBlock(text ?? string.Empty);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string? ReadText(string path) =>
        File.Exists(path) ? AutoexecText.Encoding.GetString(File.ReadAllBytes(path)) : null;

    private static void WriteText(string path, string text)
    {
        string temp = path + ".dotatune.tmp";
        File.WriteAllBytes(temp, AutoexecText.Encoding.GetBytes(text));
        File.Move(temp, path, overwrite: true);
    }
}
