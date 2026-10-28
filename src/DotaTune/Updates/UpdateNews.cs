using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Effects;
using DotaTune.Core.Pack;
using DotaTune.Core.Platform;
using DotaTune.Core.Settings;
using DotaTune.Resources;
using DotaTune.ViewModels;

namespace DotaTune.Updates;

/// <summary>
/// The first start of a newer version: a card over the window with the release notes and, when a
/// pack is installed, an offer to build it again with what it holds. All members are for the UI thread.
/// </summary>
public sealed partial class UpdateNews : ObservableObject
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly PackService _packs;
    private readonly FileLog _log;
    private readonly Func<InstallRefusal?> _rebuild;

    /// <param name="rebuild">Builds the installed pack again, see <see cref="AppServices.RebuildInstalledPack"/>.</param>
    internal UpdateNews(AppPaths paths, SettingsStore settings, PackService packs, FileLog log, Func<InstallRefusal?> rebuild)
    {
        _paths = paths;
        _settings = settings;
        _packs = packs;
        _log = log;
        _rebuild = rebuild;
    }

    [ObservableProperty]
    private bool _isOpen;

    public string Title => string.Format(CultureInfo.CurrentCulture, Strings.NewsTitleFormat, AppServices.AppVersion);

    public IReadOnlyList<NoteLine> Notes { get; private set; } = [];

    public bool HasNotes => Notes.Count > 0;

    /// <summary>A pack of an earlier version is in the game and not built again yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRebuild), nameof(ShowClose))]
    private bool _packIsOld;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRebuild), nameof(ShowClose))]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    private bool _isRebuilding;

    [ObservableProperty]
    private string? _packNote;

    public bool ShowRebuild => PackIsOld && !IsRebuilding;

    public bool ShowClose => !PackIsOld && !IsRebuilding;

    /// <summary>
    /// At start, before any page reads the settings. Opens the card on the first start of a newer
    /// version, also after a version before 0.3.0, which kept no number, and records the newest
    /// version that started.
    /// "Every effect group" is written out as the list of today's groups, so a group a later
    /// version adds stays off until the player turns it on.
    /// </summary>
    public void Detect()
    {
        bool earlierRun = File.Exists(_paths.SettingsFile) || File.Exists(_paths.InstallStateFile);
        if (!_settings.TryLoad(out AppSettings settings))
        {
            // A file this version cannot read stays as it is; the pages fall back to defaults.
            return;
        }

        string current = AppServices.AppVersion;
        bool updated = settings.LastVersion is { } last ? IsOlder(last, current) : earlierRun;
        // Never lowered: an older build started from the sources would bring the card back to the installed one.
        string record = settings.LastVersion is { } seen && IsOlder(current, seen) ? seen : current;
        if (!string.Equals(settings.LastVersion, record, StringComparison.Ordinal) || settings.EffectGroups is null)
        {
            try
            {
                _settings.Save(settings with { LastVersion = record, EffectGroups = settings.EffectGroups ?? EffectRules.All });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The card then shows again at the next start, nothing worse.
                _log.Error("Could not record the version in the settings", ex);
            }
        }

        if (!updated)
        {
            return;
        }

        Notes = ReleaseNotes.For(current);
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(HasNotes));
        try
        {
            PackIsOld = _packs.Current is not null;
        }
        catch (InvalidDataException)
        {
            // install.json is unreadable: the game page tells about it, there is nothing to rebuild from.
            PackIsOld = false;
        }

        PackNote = PackIsOld ? Strings.NewsPackOld : null;
        IsOpen = HasNotes || PackIsOld;
        _log.Info($"First start after an update from {settings.LastVersion ?? "a version before 0.3.0"} to {current}: " +
            $"{Notes.Count} note lines, {(PackIsOld ? "a pack to rebuild" : "no pack")}");
    }

    /// <summary>True when <paramref name="last"/> is an earlier version than <paramref name="current"/>; a number that does not parse is not.</summary>
    internal static bool IsOlder(string last, string current) =>
        Version.TryParse(Numbers(last), out Version? before) && Version.TryParse(Numbers(current), out Version? now) && before < now;

    private static string Numbers(string version) => version.Split('-', '+')[0];

    [RelayCommand(CanExecute = nameof(CanRebuild))]
    private async Task RebuildAsync()
    {
        IsRebuilding = true;
        PackNote = Strings.NewsRebuilding;
        try
        {
            InstallRefusal? refusal = await Task.Run(_rebuild);
            if (refusal is null)
            {
                PackIsOld = false;
                PackNote = Strings.NewsRebuilt;
            }
            else
            {
                PackNote = GameStateViewModel.RefusalText(refusal) ?? Strings.NewsRebuildRefused;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _log.Error("Pack rebuild after the update failed", ex);
            PackNote = string.Format(CultureInfo.CurrentCulture, Strings.PackErrorFormat, ex.Message);
        }
        finally
        {
            IsRebuilding = false;
        }
    }

    private bool CanRebuild => !IsRebuilding;
}
