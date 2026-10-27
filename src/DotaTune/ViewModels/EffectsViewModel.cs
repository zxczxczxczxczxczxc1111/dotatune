using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Effects;
using DotaTune.Core.Game;
using DotaTune.Core.Pack;
using DotaTune.Resources;

namespace DotaTune.ViewModels;

/// <summary>One group of effects on the page: what it removes, on or off.</summary>
public sealed partial class EffectGroupRow : ObservableObject
{
    private readonly Action _changed;

    internal EffectGroupRow(EffectGroup group, string title, string description, bool isOn, Action changed)
    {
        Group = group;
        Title = title;
        Description = description;
        _isOn = isOn;
        _changed = changed;
    }

    public EffectGroup Group { get; }

    public string Title { get; }

    public string Description { get; }

    [ObservableProperty]
    private bool _isOn;

    partial void OnIsOnChanged(bool value) => _changed();
}

/// <summary>
/// The "Effects" page: the groups the pack replaces with Valve's empty particle. A change is
/// saved at once and reaches the game when the pack is built again.
/// </summary>
public sealed partial class EffectsViewModel : ObservableObject, IActivatedPage
{
    private readonly AppServices _services;
    private GameState? _game;
    private InstallState? _pack;
    private PackHealth _health;

    internal EffectsViewModel(AppServices services, string title, string description)
    {
        _services = services;
        Title = title;
        Description = description;
        IReadOnlyList<EffectGroup> saved = services.Settings.Load().EffectGroups ?? EffectRules.All;
        Rows = [.. EffectRules.All.Select(g => new EffectGroupRow(g, GroupTitle(g), GroupDescription(g), saved.Contains(g), OnGroupsChanged))];
    }

    public string Title { get; }

    public string Description { get; }

    public IReadOnlyList<EffectGroupRow> Rows { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isOk;

    [ObservableProperty]
    private bool _isAttention;

    [ObservableProperty]
    private string? _note;

    /// <summary>"Install pack" or "Rebuild pack"; the button is hidden when the pack already matches.</summary>
    [ObservableProperty]
    private string _applyText = Strings.InstallPack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _canApplyNow;

    private bool CanApply => CanApplyNow && !IsBusy;

    public void Activated()
    {
        if (!IsBusy)
        {
            _ = RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        IsBusy = true;
        string? message;
        try
        {
            message = GameStateViewModel.RefusalText(await Task.Run(_services.InstallPack));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _services.Log.Error("Pack rebuild for effects failed", ex);
            message = string.Format(CultureInfo.CurrentCulture, Strings.PackErrorFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
        Note = message ?? Note;
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            (_game, _pack, _health) = await Task.Run(ReadPack);
            Note = BaseNote;
            Show();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private (GameState, InstallState?, PackHealth) ReadPack()
    {
        GameState game = _services.ReadGame();
        try
        {
            InstallState? pack = _services.Packs.Current;
            return (game, pack, pack is null ? PackHealth.NotInstalled : _services.Packs.Check(game));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The game page reports a broken install.json; here the pack is treated as absent.
            _services.Log.Error("Install state is unreadable on the effects page", ex);
            return (game, null, PackHealth.NotInstalled);
        }
    }

    private void OnGroupsChanged()
    {
        Show();
        List<EffectGroup> chosen = Chosen();
        try
        {
            // Null keeps "every group", so a group added in a later version is on as well.
            IReadOnlyList<EffectGroup>? stored = chosen.Count == EffectRules.All.Count ? null : chosen;
            _services.Settings.Save(_services.Settings.Load() with { EffectGroups = stored });
            Note = BaseNote;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Error("Could not save the effect groups", ex);
            Note = string.Format(CultureInfo.CurrentCulture, Strings.EffectsSaveErrorFormat, ex.Message);
        }
    }

    /// <summary>Shows how the installed pack relates to the chosen groups; the note is left to the caller.</summary>
    private void Show()
    {
        if (_game is null)
        {
            return;
        }

        List<EffectGroup> chosen = Chosen();
        bool installable = PackService.RefusalFor(_game) is null;
        if (_pack is null)
        {
            Summary = Strings.EffectsNoPack;
            (IsOk, IsAttention) = (false, false);
            ApplyText = Strings.InstallPack;
            CanApplyNow = installable && chosen.Count > 0;
            return;
        }

        // A pack that is gone, replaced or from an older build has to be built again whatever the groups.
        IEnumerable<EffectGroup> inPack = _pack.Effects?.Keys ?? [];
        bool matches = _health == PackHealth.Ready && inPack.Order().SequenceEqual(chosen.Order());
        Summary = _health switch
        {
            PackHealth.Missing => Strings.HealthMissing,
            PackHealth.Changed => Strings.HealthChanged,
            PackHealth.FolderNotLoaded => Strings.HealthFolderNotLoaded,
            PackHealth.OtherBuild => Strings.HealthOtherBuild,
            _ => matches ? Strings.EffectsPackMatches : Strings.EffectsPackDiffers,
        };
        (IsOk, IsAttention) = (matches, !matches);
        ApplyText = Strings.RebuildPack;
        CanApplyNow = !matches && installable;
    }

    private string? BaseNote => _game?.DotaRunning == true ? Strings.DotaRunningNote : null;

    private List<EffectGroup> Chosen() => [.. Rows.Where(r => r.IsOn).Select(r => r.Group)];

    private static string GroupTitle(EffectGroup group) => group switch
    {
        EffectGroup.Weather => Strings.EffectWeather,
        EffectGroup.MapAmbience => Strings.EffectMapAmbience,
        EffectGroup.BuildingAmbience => Strings.EffectBuildingAmbience,
        EffectGroup.FogMist => Strings.EffectFogMist,
        EffectGroup.EventCosmetics => Strings.EffectEventCosmetics,
        EffectGroup.CosmeticAmbience => Strings.EffectCosmeticAmbience,
        EffectGroup.Sprays => Strings.EffectSprays,
        EffectGroup.CosmeticWards => Strings.EffectCosmeticWards,
        EffectGroup.AttackFlashes => Strings.EffectAttackFlashes,
        _ => throw new InvalidOperationException($"Unknown effect group {group}"),
    };

    private static string GroupDescription(EffectGroup group) => group switch
    {
        EffectGroup.Weather => Strings.EffectWeatherDescription,
        EffectGroup.MapAmbience => Strings.EffectMapAmbienceDescription,
        EffectGroup.BuildingAmbience => Strings.EffectBuildingAmbienceDescription,
        EffectGroup.FogMist => Strings.EffectFogMistDescription,
        EffectGroup.EventCosmetics => Strings.EffectEventCosmeticsDescription,
        EffectGroup.CosmeticAmbience => Strings.EffectCosmeticAmbienceDescription,
        EffectGroup.Sprays => Strings.EffectSpraysDescription,
        EffectGroup.CosmeticWards => Strings.EffectCosmeticWardsDescription,
        EffectGroup.AttackFlashes => Strings.EffectAttackFlashesDescription,
        _ => throw new InvalidOperationException($"Unknown effect group {group}"),
    };
}
