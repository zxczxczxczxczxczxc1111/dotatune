namespace DotaTune.Core.Game;

public enum PackAction
{
    /// <summary>The voice language has its own folder: the pack goes there, nothing else changes.</summary>
    UseVoiceFolder,

    /// <summary>English voices load no folder: switch the voice language to a carrier without a voice pack.</summary>
    SwitchToCarrier,

    /// <summary>-language with a folder of its own decides: the pack goes into that folder.</summary>
    UseLaunchLanguageFolder,

    /// <summary>-language english loads no folder at all; offer English text in the game settings instead.</summary>
    OfferRemoveEnglishOverride,

    /// <summary>-language with a foreign language, usually left by Minify; offer to remove it.</summary>
    OfferRemoveForeignOverride,

    /// <summary>Every carrier has a voice pack; installing would change the voices, so nothing is done.</summary>
    NoCarrier,

    /// <summary>boot.vcfg does not exist yet: the game has to be started once.</summary>
    StartGameOnce,

    /// <summary>boot.vcfg has no AudioLanguage: Valve changed the format, nothing is written.</summary>
    UnknownBootFormat,
}

/// <param name="LoadedFolder">Language folder the game loads right now, null for none.</param>
/// <param name="TargetFolder">Where DotaTune puts its pack, null when it does not install.</param>
public sealed record PackDecision(PackAction Action, string? LoadedFolder, string? TargetFolder);

/// <summary>The table from the pack design: which folder the game loads and what DotaTune does about it.</summary>
public static class PackPlanner
{
    public const string English = "english";

    /// <summary>Carrier candidates in order of preference: folders Valve ships with every install.</summary>
    public static readonly IReadOnlyList<string> Carriers = ["russian", "schinese", "koreana"];

    public static PackDecision Decide(BootConfig? boot, string? launchLanguage, IReadOnlyList<LanguageFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        LanguageFolder? FolderOf(string? language) =>
            language is null ? null : folders.FirstOrDefault(f => string.Equals(f.Language, language, StringComparison.Ordinal));

        if (launchLanguage is not null)
        {
            if (string.Equals(launchLanguage, English, StringComparison.Ordinal))
            {
                return new PackDecision(PackAction.OfferRemoveEnglishOverride, null, null);
            }

            LanguageFolder? launchFolder = FolderOf(launchLanguage);
            bool shipped = Carriers.Contains(launchLanguage, StringComparer.Ordinal);
            return shipped && launchFolder is not null
                ? new PackDecision(PackAction.UseLaunchLanguageFolder, launchLanguage, launchLanguage)
                : new PackDecision(PackAction.OfferRemoveForeignOverride, launchFolder?.Language, null);
        }

        if (boot is null)
        {
            return new PackDecision(PackAction.StartGameOnce, null, null);
        }

        if (boot.AudioLanguage is null)
        {
            return new PackDecision(PackAction.UnknownBootFormat, null, null);
        }

        LanguageFolder? voiceFolder = string.Equals(boot.AudioLanguage, English, StringComparison.Ordinal) ? null : FolderOf(boot.AudioLanguage);
        if (voiceFolder is not null)
        {
            return new PackDecision(PackAction.UseVoiceFolder, voiceFolder.Language, voiceFolder.Language);
        }

        string? carrier = Carriers.FirstOrDefault(c => FolderOf(c) is { HasVoicePack: false });
        return carrier is null
            ? new PackDecision(PackAction.NoCarrier, null, null)
            : new PackDecision(PackAction.SwitchToCarrier, null, carrier);
    }
}
