using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DotaTune.Resources;
using DotaTune.Updates;

namespace DotaTune.ViewModels;

/// <summary>A choice of interface language; <see cref="Code"/> is null for "same as Windows".</summary>
public sealed record LanguageOption(string? Code, string Title);

/// <summary>
/// The settings page: the program's own settings, then the copy, transfer and presets of the game
/// settings. Those blocks belong to the config page's model and run there, so their writes and the
/// config's go one at a time; this page only shows them and reads the game for them when opened.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IActivatedPage
{
    private readonly AppServices _services;

    internal SettingsViewModel(AppServices services, ConfigViewModel config)
    {
        _services = services;
        Config = config;
        Languages =
        [
            new LanguageOption(null, Strings.LanguageSystem),
            new LanguageOption(UiLanguage.Russian, Strings.LanguageRussian),
            new LanguageOption(UiLanguage.English, Strings.LanguageEnglish),
        ];

        string? saved = services.Settings.Load().Language;
        _selectedLanguage = Languages.FirstOrDefault(l => string.Equals(l.Code, saved, StringComparison.Ordinal)) ?? Languages[0];
    }

    /// <summary>Owner of the copy, transfer and presets blocks shown here.</summary>
    public ConfigViewModel Config { get; }

    /// <summary>Raised with the chosen language code; the window is rebuilt in it even if saving failed.</summary>
    public event Action<string?>? LanguageChanged;

    public IReadOnlyList<LanguageOption> Languages { get; }

    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    public string DataFolder => _services.Paths.DataRoot;

    /// <summary>The state of updates and the buttons to check or install now.</summary>
    public UpdateService Updates => _services.Updates;

    public string VersionText => string.Format(CultureInfo.CurrentCulture, Strings.VersionFormat, AppServices.AppVersion);

    public void Activated() => Config.Activated();

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        try
        {
            _services.Settings.Save(_services.Settings.Load() with { Language = value.Code });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Error("Could not save the interface language", ex);
        }

        LanguageChanged?.Invoke(value.Code);
    }
}
