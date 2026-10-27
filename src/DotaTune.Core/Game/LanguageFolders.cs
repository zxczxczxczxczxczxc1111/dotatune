using System.Globalization;
using System.Text.RegularExpressions;
using ValvePak;

namespace DotaTune.Core.Game;

public enum PakOwner
{
    /// <summary>pak01: the game's own archive, in a language folder that is the voice pack.</summary>
    Valve,

    /// <summary>Has the DotaTune marker inside.</summary>
    DotaTune,

    /// <summary>Another mod manager or a hand-made mod. Never touched.</summary>
    Other,
}

public sealed record PakFile(int Slot, string FileName, PakOwner Owner);

/// <summary>A game\dota_&lt;language&gt; folder and the archives in it.</summary>
public sealed record LanguageFolder(string Language, string Path, IReadOnlyList<PakFile> Paks)
{
    /// <summary>
    /// A Valve voice pack lives in pak01. Folders without it hold only gameinfo.gi and
    /// readonly_tools_asset_info.bin, and the game takes every missing file from the base
    /// folder, so voices stay English.
    /// </summary>
    public bool HasVoicePack => Paks.Any(p => p.Owner == PakOwner.Valve);
}

public static partial class LanguageFolders
{
    public const string MarkerPath = "dotatune/pak.json";

    /// <summary>Folders of the game that are not languages.</summary>
    private static readonly HashSet<string> NotLanguages = new(StringComparer.OrdinalIgnoreCase) { "addons", "lv" };

    public static IReadOnlyList<LanguageFolder> Scan(string gameRoot)
    {
        string game = System.IO.Path.Combine(gameRoot, "game");
        var folders = new List<LanguageFolder>();
        foreach (string dir in Directory.EnumerateDirectories(game, "dota_*"))
        {
            string language = System.IO.Path.GetFileName(dir)["dota_".Length..].ToLowerInvariant();
            if (NotLanguages.Contains(language))
            {
                continue;
            }

            folders.Add(new LanguageFolder(language, dir, ScanPaks(dir)));
        }

        folders.Sort((a, b) => string.CompareOrdinal(a.Language, b.Language));
        return folders;
    }

    public static IReadOnlyList<PakFile> ScanPaks(string folder)
    {
        var paks = new List<PakFile>();
        foreach (string file in Directory.EnumerateFiles(folder, "pak*_dir.vpk"))
        {
            string name = System.IO.Path.GetFileName(file);
            Match match = PakName().Match(name);
            if (!match.Success)
            {
                continue;
            }

            int slot = int.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            paks.Add(new PakFile(slot, name, slot == 1 ? PakOwner.Valve : OwnerOf(file)));
        }

        paks.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        return paks;
    }

    private static PakOwner OwnerOf(string file)
    {
        try
        {
            using var package = new Package();
            package.Read(file);
            return package.FindEntry(MarkerPath) is null ? PakOwner.Other : PakOwner.DotaTune;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException || GameArchive.IsDamage(ex))
        {
            // A broken or locked archive is still someone's file: treat it as foreign.
            return PakOwner.Other;
        }
    }

    [GeneratedRegex(@"^pak(\d{2})_dir\.vpk$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PakName();
}
