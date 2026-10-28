using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Config;
using DotaTune.Core.Effects;
using DotaTune.Core.Game;
using DotaTune.Core.Pack;
using DotaTune.Resources;

namespace DotaTune.ViewModels;

/// <summary>
/// The "Colours" page. Mouse effects get an own colour painted into the game's own particles and
/// carried by the pack; the minimap colours are lines of the config page's autoexec block. A
/// change is saved at once, and the button puts into the game the part that differs.
/// </summary>
public sealed partial class ClicksViewModel : ObservableObject, IActivatedPage, IWorkingPage
{
    private readonly AppServices _services;
    private readonly ConfigViewModel _config;
    private readonly IReadOnlyList<MouseEffect> _effects = Enum.GetValues<MouseEffect>();
    private GameState? _game;
    private InstallState? _pack;
    private PackHealth _health;
    private AutoexecState? _written;
    private AutoexecHealth _autoexecHealth;
    private bool _packNeeded;
    private bool _minimapNeeded;

    internal ClicksViewModel(AppServices services, ConfigViewModel config, string title, string description)
    {
        _services = services;
        _config = config;
        Title = title;
        Description = description;
        MouseColorChoice saved = services.Settings.Load().MouseColors ?? new MouseColorChoice();
        IReadOnlyList<Swatch> palette = Palette();
        Rows = [.. _effects.Select(e => new ColorRow(EffectTitle(e), EffectDescription(e), WhenOn(e), saved[e], palette, OnChoiceChanged))];
        // Any config change can touch the block; only a colour change makes the last message stale.
        config.ChoiceChanged += Show;
        foreach (ColorRow row in config.Colors)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ColorRow.IsOn) or nameof(ColorRow.Color))
                {
                    Note = BaseNote;
                }
            };
        }
    }

    public string Title { get; }

    public string Description { get; }

    /// <summary>One row per <see cref="MouseEffect"/>, in its order.</summary>
    public IReadOnlyList<ColorRow> Rows { get; }

    /// <summary>The minimap rows of the config page's choice; a change there is saved by that page.</summary>
    public IReadOnlyList<ColorRow> MinimapRows => _config.Colors;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isBusy;

    bool IWorkingPage.IsWorking => IsBusy;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isOk;

    [ObservableProperty]
    private bool _isAttention;

    [ObservableProperty]
    private string? _note;

    /// <summary>"Install pack" when the first pack would go in with the colours, it carries the effects too.</summary>
    [ObservableProperty]
    private string _applyText = Strings.ColorsApply;

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
        bool pack = _packNeeded && _game is not null && PackService.RefusalFor(_game) is null;
        bool minimap = _minimapNeeded;

        // The whole block goes in: the config page's other lines are the same saved choice.
        IReadOnlyList<ConfigLine> lines = _config.Choice().Lines();
        IsBusy = true;
        string? message;
        try
        {
            message = await Task.Run(() =>
            {
                string?[] parts = [pack ? InstallPack() : null, minimap ? WriteMinimap(lines) : null];
                return parts.Any(p => p is not null) ? string.Join(" ", parts.Where(p => p is not null)) : null;
            });
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
        Note = message ?? Note;
    }

    private string? InstallPack()
    {
        try
        {
            return GameStateViewModel.RefusalText(_services.InstallPack());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _services.Log.Error("Pack rebuild for mouse colours failed", ex);
            return string.Format(CultureInfo.CurrentCulture, Strings.PackErrorFormat, ex.Message);
        }
    }

    private string? WriteMinimap(IReadOnlyList<ConfigLine> lines)
    {
        try
        {
            GameState game = _services.ReadGame();
            return ConfigViewModel.RefusalText(_services.Autoexec.Apply(game, lines), game);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            _services.Log.Error("Minimap colours were not written", ex);
            return string.Format(CultureInfo.CurrentCulture, Strings.ConfigErrorFormat, ex.Message);
        }
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            (_game, _pack, _health, _written, _autoexecHealth) = await Task.Run(ReadState);
            Show();
            Note = BaseNote;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private (GameState, InstallState?, PackHealth, AutoexecState?, AutoexecHealth) ReadState()
    {
        GameState game = _services.ReadGame();
        InstallState? pack = null;
        PackHealth health = PackHealth.NotInstalled;
        try
        {
            pack = _services.Packs.Current;
            health = pack is null ? PackHealth.NotInstalled : _services.Packs.Check(game);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The game page reports a broken install.json; here the pack is treated as absent.
            _services.Log.Error("Install state is unreadable on the colours page", ex);
        }

        try
        {
            return (game, pack, health, _services.Autoexec.Current, _services.Autoexec.Check());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The config page reports a broken autoexec.json; here nothing is treated as written.
            _services.Log.Error("autoexec state is unreadable on the colours page", ex);
            return (game, pack, health, null, AutoexecHealth.NotWritten);
        }
    }

    private void OnChoiceChanged()
    {
        Show();
        MouseColorChoice chosen = Chosen();
        try
        {
            _services.Settings.Save(_services.Settings.Load() with { MouseColors = chosen.IsEmpty ? null : chosen });
            Note = BaseNote;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Error("Could not save the mouse colours", ex);
            Note = string.Format(CultureInfo.CurrentCulture, Strings.EffectsSaveErrorFormat, ex.Message);
        }
    }

    /// <summary>Shows how the pack and the autoexec block relate to the chosen colours; the note is left to the caller.</summary>
    private void Show()
    {
        if (_game is null)
        {
            return;
        }

        MouseColorChoice chosen = Chosen();
        IReadOnlyList<ConfigLine> minimap = MinimapLines(_config.Choice().Lines());
        IReadOnlyList<ConfigLine> written = _written is null ? [] : MinimapLines(_written.Lines);
        _minimapNeeded = !minimap.SequenceEqual(written) || (minimap.Count > 0 && _autoexecHealth != AutoexecHealth.Ready);

        // A pack that is gone, replaced or from an older build has to be built again whatever the colours.
        bool packBroken = _pack is not null && _health != PackHealth.Ready;
        _packNeeded = _pack is null ? !chosen.IsEmpty : packBroken || (_pack.MouseColors ?? new MouseColorChoice()) != chosen;
        bool needed = _packNeeded || _minimapNeeded;
        bool chosenAny = !chosen.IsEmpty || minimap.Count > 0;
        Summary = !packBroken ? (needed ? Strings.ColorsNotApplied : chosenAny ? Strings.ColorsApplied : Strings.ColorsAsInGame)
            : _health switch
            {
                PackHealth.Missing => Strings.HealthMissing,
                PackHealth.Changed => Strings.HealthChanged,
                PackHealth.FolderNotLoaded => Strings.HealthFolderNotLoaded,
                PackHealth.OtherBuild => Strings.HealthOtherBuild,
                _ => Strings.ColorsNotApplied,
            };
        (IsOk, IsAttention) = (!needed && chosenAny, needed);
        ApplyText = _pack is null && _packNeeded ? Strings.InstallPack : Strings.ColorsApply;
        CanApplyNow = (_packNeeded && PackService.RefusalFor(_game) is null)
            || (_minimapNeeded && _game.Dota is not null && !_game.DotaRunning);
    }

    private static List<ConfigLine> MinimapLines(IEnumerable<ConfigLine> lines) =>
        [.. lines.Where(l => GameConfig.ColorNames.Contains(l.Name))];

    /// <summary>
    /// While the pack matches the choice: the effects it was built for but could not recolour,
    /// because the game has no such particles any more. Rebuilding would not change that.
    /// </summary>
    private string? NotTaken()
    {
        if (_packNeeded || _pack?.MouseNotTaken is not { Count: > 0 } missing)
        {
            return null;
        }

        return string.Format(CultureInfo.CurrentCulture, Strings.ClicksNotTakenFormat, string.Join(", ", missing.Select(EffectTitle)));
    }

    private string? BaseNote => _game?.DotaRunning == true ? Strings.ColorsDotaRunning : NotTaken();

    private MouseColorChoice Chosen() =>
        _effects.Zip(Rows).Aggregate(new MouseColorChoice(), (choice, pair) => choice.With(pair.First, pair.Second.Value));

    // The colour a row starts with when it is switched on: one the game itself does not use for that effect.
    private static Rgb WhenOn(MouseEffect effect) => effect switch
    {
        MouseEffect.MoveClick => new Rgb(160, 96, 255),
        MouseEffect.AttackClick => new Rgb(255, 140, 0),
        MouseEffect.UnitClick => new Rgb(0, 255, 255),
        MouseEffect.SelectionRings => new Rgb(255, 0, 255),
        MouseEffect.HeroHighlight => new Rgb(255, 255, 0),
        MouseEffect.TowerRange => new Rgb(33, 161, 255),
        _ => throw new InvalidOperationException($"Unknown mouse effect {effect}"),
    };

    private static string EffectTitle(MouseEffect effect) => effect switch
    {
        MouseEffect.MoveClick => Strings.ClicksMove,
        MouseEffect.AttackClick => Strings.ClicksAttack,
        MouseEffect.UnitClick => Strings.ClicksUnit,
        MouseEffect.SelectionRings => Strings.ClicksRings,
        MouseEffect.HeroHighlight => Strings.ClicksHero,
        MouseEffect.TowerRange => Strings.ClicksTower,
        _ => throw new InvalidOperationException($"Unknown mouse effect {effect}"),
    };

    private static string EffectDescription(MouseEffect effect) => effect switch
    {
        MouseEffect.MoveClick => Strings.ClicksMoveDescription,
        MouseEffect.AttackClick => Strings.ClicksAttackDescription,
        MouseEffect.UnitClick => Strings.ClicksUnitDescription,
        MouseEffect.SelectionRings => Strings.ClicksRingsDescription,
        MouseEffect.HeroHighlight => Strings.ClicksHeroDescription,
        MouseEffect.TowerRange => Strings.ClicksTowerDescription,
        _ => throw new InvalidOperationException($"Unknown mouse effect {effect}"),
    };

    /// <summary>Bright colours that stand out on the ground, the ones mod catalogues offer for clicks.</summary>
    private static List<Swatch> Palette() =>
    [
        new(new Rgb(160, 96, 255), Strings.ColorPurple),
        new(new Rgb(255, 0, 255), Strings.ColorMagenta),
        new(new Rgb(33, 161, 255), Strings.ColorBlue),
        new(new Rgb(0, 255, 255), Strings.ColorCyan),
        new(new Rgb(0, 255, 0), Strings.ColorGreen),
        new(new Rgb(255, 255, 0), Strings.ColorYellow),
        new(new Rgb(255, 140, 0), Strings.ColorOrange),
        new(new Rgb(255, 0, 0), Strings.ColorRed),
        new(new Rgb(255, 255, 255), Strings.ColorWhite),
    ];
}
