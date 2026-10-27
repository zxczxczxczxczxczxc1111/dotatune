using DotaTune.Core.Steam;

namespace DotaTune.Core.Game;

/// <summary>
/// game\dota\cfg\boot.vcfg. The game creates it on first start and rewrites it on exit;
/// its AudioLanguage picks the language folder the game loads, not the text language.
/// </summary>
public sealed record BootConfig(string? AudioLanguage, string? UiLanguage)
{
    public static string PathIn(string gameRoot) => Path.Combine(gameRoot, "game", "dota", "cfg", "boot.vcfg");

    /// <summary>Null when the file does not exist: the game was never started after install or reset.</summary>
    public static BootConfig? Read(string gameRoot)
    {
        string file = PathIn(gameRoot);
        if (!File.Exists(file))
        {
            return null;
        }

        var root = KeyValues.Load(file);
        return new BootConfig(
            root.Child("AudioLanguage").Text()?.ToLowerInvariant(),
            root.Child("UILanguage").Text()?.ToLowerInvariant());
    }
}
