using System.Text;

namespace DotaTune.Core.Steam;

/// <summary>One launch option with the values that follow it: "-map dota", "+fps_max 0", "-novid".</summary>
public sealed record LaunchOption(string Name, IReadOnlyList<string> Values)
{
    public override string ToString() => Values.Count == 0 ? Name : $"{Name} {string.Join(' ', Values)}";
}

/// <summary>What the "Config and launch" page changes in a launch line, and nothing else.</summary>
public static class LaunchOptionsEditor
{
    /// <summary>
    /// Options the current game does not know: their MurmurHash2 is in none of its binaries
    /// (checked on build 6967, docs\dota-mody-razbor.html) and -prewarm was removed in 2020.
    /// </summary>
    public static IReadOnlySet<string> DeadOptions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "-prewarm", "-map", "-novr", "-dota_embers", "-nod3d9ex", "-noaafonts", "-english", "-dx9", "-gl", "-useallavailablecores",
    };

    public const string NoVideo = "-novid";
    public const string NoJoystick = "-nojoy";

    /// <summary>The game writes its console into game\dota\console.log, where DotaTune finds MMR readings.</summary>
    public const string ConsoleLog = "-condebug";

    /// <summary>Options in order; words before the first option are kept as one with an empty name.</summary>
    public static IReadOnlyList<LaunchOption> Parse(string? options)
    {
        var result = new List<LaunchOption>();
        string? name = null;
        var values = new List<string>();
        foreach (string token in LaunchOptions.Tokenize(options ?? string.Empty))
        {
            if (token.Length > 1 && token[0] is '-' or '+' && !double.TryParse(token, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                Flush();
                name = token;
            }
            else
            {
                values.Add(token);
            }
        }

        Flush();
        return result;

        void Flush()
        {
            if (name is not null || values.Count > 0)
            {
                result.Add(new LaunchOption(name ?? string.Empty, [.. values]));
            }

            name = null;
            values.Clear();
        }
    }

    public static bool Has(IReadOnlyList<LaunchOption> options, string name) =>
        options.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The line with dead options taken out if asked, and -novid, -nojoy and -condebug present or
    /// absent as chosen; null leaves -condebug as it is. Every other option keeps its place and
    /// its values; a line that needs no change comes back as it is, spaces and quotes included.
    /// </summary>
    public static string Plan(string? current, bool noVideo, bool noJoystick, bool removeDead, bool? consoleLog = null)
    {
        IReadOnlyList<LaunchOption> original = Parse(current);
        List<LaunchOption> options = [.. original];
        if (removeDead)
        {
            options.RemoveAll(o => DeadOptions.Contains(o.Name));
        }

        Toggle(NoVideo, noVideo);
        Toggle(NoJoystick, noJoystick);
        if (consoleLog is { } log)
        {
            Toggle(ConsoleLog, log);
        }

        return options.SequenceEqual(original) ? current ?? string.Empty : Join(options);

        void Toggle(string name, bool on)
        {
            if (!on)
            {
                options.RemoveAll(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            else if (!Has(options, name))
            {
                options.Add(new LaunchOption(name, []));
            }
        }
    }

    /// <summary>Back to one line; a value with a space or nothing in it is quoted as Steam passes it on.</summary>
    public static string Join(IEnumerable<LaunchOption> options)
    {
        var line = new StringBuilder();
        foreach (LaunchOption option in options)
        {
            foreach (string token in (option.Name.Length > 0 ? [option.Name] : Array.Empty<string>()).Concat(option.Values))
            {
                if (line.Length > 0)
                {
                    line.Append(' ');
                }

                line.Append(token.Length == 0 || token.Any(char.IsWhiteSpace) ? $"\"{token}\"" : token);
            }
        }

        return line.ToString();
    }
}

/// <summary>
/// Writes the Dota launch options into userdata\&lt;id&gt;\config\localconfig.vdf. Only the value
/// of LaunchOptions in the "570" block changes; every other byte stays as Steam wrote it.
/// Steam keeps this file in memory and writes it back on exit, so it is changed only while
/// Steam is closed.
/// </summary>
public static class LocalConfigEditor
{
    private static readonly string[] AppPath = ["software", "valve", "steam", "apps", LaunchOptions.DotaAppId];

    /// <summary>False when the account has no Dota block in the file yet (the game was never started on it).</summary>
    /// <exception cref="InvalidDataException">The file is not KeyValues text.</exception>
    public static bool SetLaunchOptions(string file, string options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Any(c => c is '\r' or '\n'))
        {
            throw new ArgumentException("Launch options are one line", nameof(options));
        }

        string text = Encoding.Latin1.GetString(File.ReadAllBytes(file));
        (int Start, int End)? value = null;
        int blockEnd = -1;
        int blockDepth = 0;
        var path = new List<string>();
        string? key = null;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                int newline = text.IndexOf('\n', i);
                i = newline < 0 ? text.Length : newline;
            }
            else if (c == '{')
            {
                path.Add((key ?? string.Empty).ToLowerInvariant());
                key = null;
                i++;
            }
            else if (c == '}')
            {
                if (path.Count == 0)
                {
                    throw new InvalidDataException($"{file}: unbalanced braces");
                }

                if (InAppBlock(path) && blockEnd < 0)
                {
                    blockEnd = i;
                    blockDepth = path.Count;
                }

                path.RemoveAt(path.Count - 1);
                key = null;
                i++;
            }
            else if (c == '"')
            {
                int start = i + 1;
                int end = start;
                while (end < text.Length && text[end] != '"')
                {
                    end += text[end] == '\\' ? 2 : 1;
                }

                if (end >= text.Length)
                {
                    throw new InvalidDataException($"{file}: a string is not closed");
                }

                if (key is null)
                {
                    key = text[start..end];
                }
                else
                {
                    if (InAppBlock(path) && string.Equals(key, "LaunchOptions", StringComparison.OrdinalIgnoreCase))
                    {
                        value = (start, end);
                    }

                    key = null;
                }

                i = end + 1;
            }
            else
            {
                throw new InvalidDataException($"{file}: unexpected '{c}' at {i}");
            }
        }

        if (path.Count != 0 || key is not null)
        {
            throw new InvalidDataException($"{file}: the file ends inside a block");
        }

        string escaped = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(
            options.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)));
        string changed;
        if (value is (int s, int e))
        {
            changed = string.Concat(text.AsSpan(0, s), escaped, text.AsSpan(e));
        }
        else if (blockEnd >= 0)
        {
            int lineStart = text.LastIndexOf('\n', blockEnd) + 1;
            string newline = lineStart >= 2 && text[lineStart - 2] == '\r' ? "\r\n" : "\n";
            string line = $"{new string('\t', blockDepth)}\"LaunchOptions\"\t\t\"{escaped}\"{newline}";
            changed = text.Insert(lineStart, line);
        }
        else
        {
            return false;
        }

        string temp = file + ".dotatune.tmp";
        File.WriteAllBytes(temp, Encoding.Latin1.GetBytes(changed));
        File.Move(temp, file, overwrite: true);
        return true;
    }

    /// <summary>Inside UserLocalConfigStore\Software\Valve\Steam\apps\570, whatever the root is called.</summary>
    private static bool InAppBlock(List<string> path) =>
        path.Count == AppPath.Length + 1 && path.Skip(1).SequenceEqual(AppPath, StringComparer.Ordinal);
}
