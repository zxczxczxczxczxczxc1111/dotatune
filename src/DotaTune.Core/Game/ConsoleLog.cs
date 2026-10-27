using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DotaTune.Core.Game;

/// <summary>The GAME search paths in the order the game reads them.</summary>
public sealed partial record SearchPaths(IReadOnlyList<string> Game)
{
    /// <summary>Whether the game loaded pak NN of a language folder. The archive is listed without "_dir",
    /// and only the ASCII tail is compared: how the game writes a Cyrillic game folder is not known.</summary>
    public bool HasPak(string language, int slot)
    {
        string tail = string.Create(CultureInfo.InvariantCulture, $@"\game\dota_{language}\pak{slot:D2}.vpk");
        return Game.Any(path => path.EndsWith(tail, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Language folders among the search paths: "russian" for game\dota_russian\.</summary>
    public IReadOnlyList<string> LanguageFolders =>
        [.. Game.Select(path => LanguageFolder().Match(path)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    [GeneratedRegex(@"\\game\\dota_([a-z]+)\\$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageFolder();
}

/// <param name="Head">The first bytes of the file: the game may append to it or start it anew.</param>
public sealed record LogMark(long Length, byte[] Head)
{
    public static LogMark None { get; } = new(0, []);
}

/// <summary>
/// game/dota/console.log, written when the game runs with -condebug. At start the game prints
/// its search paths, one per line:
/// <c>10/26 10:46:52 [Filesystem] GAME   "D:\...\game\dota_russian\pak42.vpk" (vpk) ...</c>.
/// </summary>
public static partial class ConsoleLog
{
    private const int HeadSize = 64;

    public static string PathIn(string gameRoot) => Path.Combine(gameRoot, "game", "dota", "console.log");

    /// <summary>Where the log of the previous session ends and how it begins.</summary>
    public static LogMark MarkOf(string file)
    {
        if (!File.Exists(file))
        {
            return LogMark.None;
        }

        using FileStream stream = Open(file);
        return new LogMark(stream.Length, ReadHead(stream, HeadSize));
    }

    /// <summary>
    /// What the game wrote after the mark. A file that no longer begins the way it did, or is
    /// shorter, was started anew and is read whole. The game keeps the file open while it runs.
    /// </summary>
    public static string ReadAfter(string file, LogMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        if (!File.Exists(file))
        {
            return string.Empty;
        }

        using FileStream stream = Open(file);
        bool appended = stream.Length >= mark.Length && ReadHead(stream, mark.Head.Length).AsSpan().SequenceEqual(mark.Head);
        stream.Seek(appended ? mark.Length : 0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd();
    }

    private static FileStream Open(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static byte[] ReadHead(FileStream stream, int size)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var head = new byte[Math.Min(size, stream.Length)];
        stream.ReadExactly(head);
        return head;
    }

    /// <summary>The first list of search paths in the text, or null while the game is still printing it.</summary>
    public static SearchPaths? FindSearchPaths(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var game = new List<string>();
        bool inList = false;
        string[] lines = text.Split('\n');
        // The last piece may be a line the game is still writing.
        foreach (string line in lines.Take(lines.Length - 1))
        {
            Match match = PathLine().Match(line);
            if (match.Success)
            {
                inList = true;
                if (match.Groups["id"].Value == "GAME")
                {
                    game.Add(match.Groups["path"].Value);
                }
            }
            else if (inList && !line.Contains("[Filesystem]", StringComparison.Ordinal) && line.Trim().Length > 0)
            {
                return new SearchPaths(game);
            }
        }

        return null;
    }

    [GeneratedRegex(@"\[Filesystem\]\s+(?<id>[A-Z_]+)\s+""(?<path>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex PathLine();
}
