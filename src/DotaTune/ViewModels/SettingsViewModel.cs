using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DotaTune.Resources;
using DotaTune.Updates;

namespace DotaTune.ViewModels;

/// <summary>A choice of interface language; <see cref="Code"/> is null for "same as Windows".</summary>
public sealed record LanguageOption(string? Code, string Title);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;

    internal SettingsViewModel(AppServices services)
    {
        _services = services;
        Languages =
        [
            new LanguageOption(null, Strings.LanguageSystem),
            new LanguageOption(UiLanguage.Russian, Strings.LanguageRussian),
            new LanguageOption(UiLanguage.English, Strings.LanguageEnglish),
        ];

        string? saved = services.Settings.Load().Language;
        _selectedLanguage = Languages.FirstOrDefault(l => string.Equals(l.Code, saved, StringComparison.Ordinal)) ?? Languages[0];
    }

    /// <summary>Raised with the chosen language code; the window is rebuilt in it even if saving failed.</summary>
    public event Action<string?>? LanguageChanged;

    public IReadOnlyList<LanguageOption> Languages { get; }

    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    public string DataFolder => _services.Paths.DataRoot;

    /// <summary>The state of updates and the buttons to check or install now.</summary>
    public UpdateService Updates => _services.Updates;

    public string VersionText => string.Format(CultureInfo.CurrentCulture, Strings.VersionFormat, AppServices.AppVersion);

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
