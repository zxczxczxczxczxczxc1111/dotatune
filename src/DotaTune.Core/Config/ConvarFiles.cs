using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DotaTune.Core.Steam;

namespace DotaTune.Core.Config;

/// <summary>
/// Files where the game keeps the values it saves (commands with the archive flag):
/// machine_convars for this computer and user_convars of the account, which the game writes
/// in three copies at once. A value set in autoexec ends up here and outlives the line.
/// </summary>
public static partial class ConvarFiles
{
    public static IReadOnlyList<string> RelativePaths { get; } =
    [
        Path.Combine("local", "cfg", "machine_convars.vcfg"),
        Path.Combine("remote", "user_convars.vcfg"),
        Path.Combine("local", "cfg", "user_convars_0_slot0.vcfg"),
        Path.Combine("local", "cfg", "user_convars_0_slot0.vcfg_lastclouded"),
    ];

    /// <summary>userdata\&lt;id&gt;\570 of every account on this computer that has started Dota.</summary>
    public static IReadOnlyList<string> GameFolders(string steamRoot)
    {
        string userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateDirectories(userdata)
                .Where(d => uint.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .Select(d => Path.Combine(d, LaunchOptions.DotaAppId))
                .Where(Directory.Exists)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>The convar files a game folder has.</summary>
    public static IEnumerable<string> FilesIn(string gameFolder) =>
        RelativePaths.Select(p => Path.Combine(gameFolder, p)).Where(File.Exists);

    /// <summary>Value of one convar; null when the file does not have it.</summary>
    public static string? Read(string file, string name) => Read(file, [name])[name];

    /// <summary>Values of several convars from one reading of the file; null for those it does not have.</summary>
    public static IReadOnlyDictionary<string, string?> Read(string file, IEnumerable<string> names)
    {
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(file));
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            values[name] = Find(text, name)?.Groups["value"].Value;
        }

        return values;
    }

    /// <summary>
    /// Sets the value of a convar the file already has, or takes its line out when the value
    /// is null. Every other byte stays as the game wrote it. False when there was no such line.
    /// </summary>
    public static bool Write(string file, string name, string? value)
    {
        if (value is not null && value.Any(c => c is '"' or '\r' or '\n'))
        {
            throw new ArgumentException($"Not a convar value: {value}", nameof(value));
        }

        string text = Encoding.Latin1.GetString(File.ReadAllBytes(file));
        if (Find(text, name) is not Match line)
        {
            return false;
        }

        string changed;
        if (value is null)
        {
            int end = line.Index + line.Length;
            if (text.AsSpan(end).StartsWith("\r\n", StringComparison.Ordinal))
            {
                end += 2;
            }
            else if (end < text.Length && text[end] == '\n')
            {
                end++;
            }

            changed = string.Concat(text.AsSpan(0, line.Index), text.AsSpan(end));
        }
        else
        {
            Group old = line.Groups["value"];
            changed = string.Concat(text.AsSpan(0, old.Index), value, text.AsSpan(old.Index + old.Length));
        }

        string temp = file + ".dotatune.tmp";
        File.WriteAllBytes(temp, Encoding.Latin1.GetBytes(changed));
        File.Move(temp, file, overwrite: true);
        return true;
    }

    /// <summary>
    /// Whether a saved value is the one written in autoexec: the game saves "1" as "true" and
    /// may print numbers in another form.
    /// </summary>
    public static bool SameValue(string saved, string written)
    {
        string a = Normalize(saved);
        string b = Normalize(written);
        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
        {
            return Math.Abs(x - y) < 1e-6;
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static string Normalize(string value) => value.Trim() switch
        {
            var v when v.Equals("true", StringComparison.OrdinalIgnoreCase) => "1",
            var v when v.Equals("false", StringComparison.OrdinalIgnoreCase) => "0",
            var v => v,
        };
    }

    private static Match? Find(string text, string name)
    {
        foreach (Match line in ConvarLine().Matches(text))
        {
            if (string.Equals(line.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }

        return null;
    }

    // "name"<tabs>"value" on a line of its own; \r stays outside the match so the line break is kept.
    [GeneratedRegex("^[ \\t]*\"(?<name>[^\"\\r\\n]+)\"[ \\t]+\"(?<value>[^\"\\r\\n]*)\"[ \\t]*(?=\\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ConvarLine();
}
