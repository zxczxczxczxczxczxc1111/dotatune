using System.Text;
using System.Text.Json;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Effects;

namespace DotaTune.Core.Pack;

/// <param name="Language">Language folder the pack is in: "russian" for game\dota_russian.</param>
/// <param name="PreviousAudioLanguage">Voice language before DotaTune switched it; null when it was not touched.</param>
/// <param name="Effects">How many particles of each group the installed pack replaces; null for a pack without effects.</param>
/// <param name="MenuBackground">Id of the main menu background in the installed pack; null for the game's own.</param>
/// <param name="MouseColors">The mouse colours chosen when the pack was built; null when none was.</param>
/// <param name="MouseNotTaken">Those of them the pack could not recolour, see <see cref="BuiltPack.MouseNotTaken"/>.</param>
/// <param name="Mmr">The installed pack puts the MMR line into the menu and the profile.</param>
/// <param name="AutoAccept">Seconds after which the installed pack accepts a found match; null when it does not.</param>
/// <param name="HideEvents">The installed pack hides the event tiles of the home page.</param>
public sealed record InstallState(
    string GameRoot,
    string Language,
    int Slot,
    string Sha1,
    string? PreviousAudioLanguage,
    string? GameBuild,
    DateTimeOffset InstalledAt,
    IReadOnlyDictionary<EffectGroup, int>? Effects = null,
    string? MenuBackground = null,
    MouseColorChoice? MouseColors = null,
    IReadOnlyList<MouseEffect>? MouseNotTaken = null,
    bool Mmr = false,
    int? AutoAccept = null,
    bool HideEvents = false)
{
    public string PakPath => Path.Combine(GameRoot, "game", $"dota_{Language}", PakFileName(Slot));

    public static string PakFileName(int slot) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"pak{slot:D2}_dir.vpk");
}

/// <summary>
/// install.json next to the settings. It lives outside the install folder so the
/// uninstall hook still finds it, and is the only record of what DotaTune changed.
/// </summary>
public sealed class InstallStateStore(string file, FileLog log)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public InstallState? Load()
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file, Utf8NoBom), PackJsonContext.Default.InstallState);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Error($"Install state {file} is unreadable", ex);
            throw new InvalidDataException($"{file} is unreadable: {ex.Message}", ex);
        }
    }

    public void Save(InstallState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, PackJsonContext.Default.InstallState), Utf8NoBom);
        File.Move(temp, file, overwrite: true);
    }

    public void Clear() => File.Delete(file);
}
