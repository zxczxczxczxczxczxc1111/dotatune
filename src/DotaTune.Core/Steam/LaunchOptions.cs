using System.Globalization;
using System.Text;

namespace DotaTune.Core.Steam;

/// <summary>Dota launch options of one Steam account. Each account has its own; the game files are shared.</summary>
public static class LaunchOptions
{
    public const string DotaAppId = "570";

    public static string LocalConfigPath(string steamRoot, uint accountId) =>
        Path.Combine(steamRoot, "userdata", accountId.ToString(CultureInfo.InvariantCulture), "config", "localconfig.vdf");

    /// <summary>
    /// Launch options from userdata\&lt;id&gt;\config\localconfig.vdf. Empty string when the
    /// account has none, null when the file does not exist yet.
    /// </summary>
    public static string? Read(string steamRoot, uint accountId) => ReadFile(LocalConfigPath(steamRoot, accountId));

    /// <summary>Launch options from the given localconfig.vdf; null when it does not exist.</summary>
    public static string? ReadFile(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        return KeyValues.Load(file)
            .Path("Software", "Valve", "Steam", "apps", DotaAppId, "LaunchOptions")
            .Text() ?? string.Empty;
    }

    /// <summary>Splits options the way the game sees them: by spaces, double quotes keep a value together.</summary>
    public static IReadOnlyList<string> Tokenize(string options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        foreach (char c in options)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                Flush();
            }
            else
            {
                current.Append(c);
            }
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
    }

    /// <summary>Value of the last -language option, in lower case; null when there is none.</summary>
    public static string? LanguageOverride(string? options)
    {
        if (string.IsNullOrWhiteSpace(options))
        {
            return null;
        }

        IReadOnlyList<string> tokens = Tokenize(options);
        string? language = null;
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            if (string.Equals(tokens[i], "-language", StringComparison.OrdinalIgnoreCase))
            {
                language = tokens[i + 1].ToLowerInvariant();
            }
        }

        return language;
    }
}
