using System.Text;
using System.Text.RegularExpressions;

namespace DotaTune.Core.Game;

/// <summary>
/// Changes only the value of AudioLanguage in boot.vcfg. Every other byte stays as the
/// game wrote it, including UILanguage that the game adds on exit and the line endings.
/// </summary>
public static partial class BootConfigEditor
{
    public static void SetAudioLanguage(string file, string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (!language.All(c => c is >= 'a' and <= 'z'))
        {
            throw new ArgumentException($"Not a game language code: {language}", nameof(language));
        }

        byte[] bytes = File.ReadAllBytes(file);
        string text = Encoding.Latin1.GetString(bytes);
        MatchCollection matches = AudioLanguageValue().Matches(text);
        if (matches.Count != 1)
        {
            throw new InvalidDataException($"{file} has {matches.Count} AudioLanguage values, expected one");
        }

        Group value = matches[0].Groups["value"];
        string changed = string.Concat(text.AsSpan(0, value.Index), language, text.AsSpan(value.Index + value.Length));
        string temp = file + ".dotatune.tmp";
        File.WriteAllBytes(temp, Encoding.Latin1.GetBytes(changed));
        File.Move(temp, file, overwrite: true);
    }

    [GeneratedRegex("\"AudioLanguage\"\\s+\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AudioLanguageValue();
}
