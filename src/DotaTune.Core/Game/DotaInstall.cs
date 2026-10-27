using DotaTune.Core.Steam;

namespace DotaTune.Core.Game;

/// <summary>Where Dota 2 is installed and which build it is.</summary>
public sealed record DotaInstall(string GameRoot, string Library, string? Build)
{
    /// <summary>game\dota: the base folder of the game.</summary>
    public string DotaFolder => Path.Combine(GameRoot, "game", "dota");

    /// <summary>Finds the library with appmanifest_570.acf and the game folder it names.</summary>
    public static DotaInstall? Find(IEnumerable<string> libraries)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        foreach (string library in libraries)
        {
            string manifest = Path.Combine(library, "steamapps", $"appmanifest_{LaunchOptions.DotaAppId}.acf");
            if (!File.Exists(manifest))
            {
                continue;
            }

            string installDir = KeyValues.Load(manifest).Child("installdir").Text() ?? "dota 2 beta";
            string root = Path.Combine(library, "steamapps", "common", installDir);
            if (Directory.Exists(Path.Combine(root, "game", "dota")))
            {
                return new DotaInstall(root, library, ReadBuild(root));
            }
        }

        return null;
    }

    /// <summary>ClientVersion from game\dota\steam.inf, the build number the game shows in its console.</summary>
    public static string? ReadBuild(string gameRoot)
    {
        string file = Path.Combine(gameRoot, "game", "dota", "steam.inf");
        if (!File.Exists(file))
        {
            return null;
        }

        foreach (string line in File.ReadLines(file))
        {
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && string.Equals(line[..eq].Trim(), "ClientVersion", StringComparison.OrdinalIgnoreCase))
            {
                return line[(eq + 1)..].Trim();
            }
        }

        return null;
    }
}
