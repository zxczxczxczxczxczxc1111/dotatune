using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Config;
using DotaTune.Core.Effects;
using DotaTune.Core.Game;
using DotaTune.Core.Presets;
using DotaTune.Core.Steam;
using DotaTune.Resources;

namespace DotaTune.ViewModels;

/// <summary>A colour on offer for the minimap.</summary>
public sealed class Swatch(Rgb color, string name)
{
    public Rgb Color { get; } = color;

    public string Name { get; } = name;

    public IBrush Brush { get; } = new SolidColorBrush(Avalonia.Media.Color.FromRgb(color.R, color.G, color.B));

    /// <summary>What a screen reader says for the item of the palette.</summary>
    public override string ToString() => Name;
}

/// <summary>A value of a drop-down list; null is "as in the game".</summary>
public sealed record NumberOption(double? Value, string Text);

/// <summary>One side on the minimap: own colour or the game's, picked from the palette or typed as a code.</summary>
public sealed partial class ColorRow : ObservableObject
{
    private readonly Action _changed;
    private bool _syncing;

    internal ColorRow(string title, string description, Rgb whenOn, Rgb? saved, IReadOnlyList<Swatch> palette, Action changed)
    {
        Title = title;
        Description = description;
        Swatches = palette;
        _changed = changed;
        _isOn = saved is not null;
        _color = saved ?? whenOn;
        _hex = _color.Hex;
        _selectedSwatch = palette.FirstOrDefault(s => s.Color == _color);
        _brush = BrushOf(_color);
        _pickerColor = HsvOf(_color);
        _pickerHex = _hex;
        _pickerBrush = _brush;
    }

    public string Title { get; }

    public string Description { get; }

    public IReadOnlyList<Swatch> Swatches { get; }

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom))]
    private Swatch? _selectedSwatch;

    /// <summary>"#RRGGBB"; text that is not a colour yet changes nothing.</summary>
    [ObservableProperty]
    private string _hex;

    [ObservableProperty]
    private Rgb _color;

    /// <summary>The current colour, also when it is not in the palette.</summary>
    [ObservableProperty]
    private IBrush _brush;

    /// <summary>
    /// The colour in the open picker, in HSV so the hue survives white and grey. It becomes the
    /// row's colour when the picker closes, not on every move of the pointer.
    /// </summary>
    [ObservableProperty]
    private HsvColor _pickerColor;

    /// <summary>"#RRGGBB" of the picker; text that is not a colour yet changes nothing.</summary>
    [ObservableProperty]
    private string _pickerHex;

    [ObservableProperty]
    private IBrush _pickerBrush;

    public Rgb? Value => IsOn ? Color : null;

    /// <summary>A colour that is not in the palette; it is shown inside the picker button.</summary>
    public bool IsCustom => SelectedSwatch is null;

    /// <summary>
    /// Makes the colour of the closed picker the row's colour; the same colour changes nothing.
    /// A code left half typed gives way to the code of that colour.
    /// </summary>
    internal void TakePicked()
    {
        Rgb picked = RgbOf(PickerColor);
        Color = picked;
        if (Rgb.ParseHex(PickerHex) != picked)
        {
            PickerHex = picked.Hex;
        }
    }

    partial void OnPickerColorChanged(HsvColor value)
    {
        Rgb color = RgbOf(value);
        PickerBrush = BrushOf(color);
        if (!_syncing && Rgb.ParseHex(PickerHex) != color)
        {
            _syncing = true;
            try
            {
                PickerHex = color.Hex;
            }
            finally
            {
                _syncing = false;
            }
        }
    }

    partial void OnPickerHexChanged(string value)
    {
        if (!_syncing && Rgb.ParseHex(value) is Rgb typed && RgbOf(PickerColor) != typed)
        {
            _syncing = true;
            try
            {
                PickerColor = HsvOf(typed);
            }
            finally
            {
                _syncing = false;
            }
        }
    }

    partial void OnIsOnChanged(bool value) => _changed();

    partial void OnSelectedSwatchChanged(Swatch? value)
    {
        if (!_syncing && value is not null)
        {
            Color = value.Color;
        }
    }

    partial void OnHexChanged(string value)
    {
        if (!_syncing && Rgb.ParseHex(value) is Rgb typed)
        {
            Color = typed;
        }
    }

    partial void OnColorChanged(Rgb value)
    {
        _syncing = true;
        try
        {
            if (Rgb.ParseHex(Hex) != value)
            {
                Hex = value.Hex;
            }

            SelectedSwatch = Swatches.FirstOrDefault(s => s.Color == value);
            Brush = BrushOf(value);
            if (RgbOf(PickerColor) != value)
            {
                PickerColor = HsvOf(value);
                PickerHex = value.Hex;
            }
        }
        finally
        {
            _syncing = false;
        }

        _changed();
    }

    /// <summary>Takes a colour from elsewhere (a preset); null is "as in the game".</summary>
    internal void Set(Rgb? value)
    {
        if (value is Rgb color)
        {
            Color = color;
        }

        IsOn = value is not null;
    }

    private static SolidColorBrush BrushOf(Rgb color) => new(Avalonia.Media.Color.FromRgb(color.R, color.G, color.B));

    private static HsvColor HsvOf(Rgb color) => Avalonia.Media.Color.FromRgb(color.R, color.G, color.B).ToHsv();

    private static Rgb RgbOf(HsvColor color)
    {
        Avalonia.Media.Color rgb = color.ToRgb();
        return new Rgb(rgb.R, rgb.G, rgb.B);
    }
}

/// <summary>
/// The "Config and launch" page. The minimap and command block goes into autoexec.cfg: a change
/// is saved at once and reaches the game when it is written again. The minimap colours belong to
/// the block but are shown on the colours page.
/// </summary>
public sealed partial class ConfigViewModel : ObservableObject, IActivatedPage
{
    private readonly AppServices _services;
    private GameState? _game;
    private AutoexecState? _written;
    private AutoexecHealth _health;

    internal ConfigViewModel(AppServices services, string title, string description)
    {
        _services = services;
        Title = title;
        Description = description;
        GameConfig saved = services.Settings.Load().Config ?? new GameConfig();
        IReadOnlyList<Swatch> palette = Palette();
        Colors =
        [
            new ColorRow(Strings.ConfigFriendly, Strings.ConfigFriendlyDescription, new Rgb(0, 255, 255), saved.FriendlyColor, palette, OnChoiceChanged),
            new ColorRow(Strings.ConfigEnemy, Strings.ConfigEnemyDescription, new Rgb(255, 0, 255), saved.EnemyColor, palette, OnChoiceChanged),
            new ColorRow(Strings.ConfigNeutral, Strings.ConfigNeutralDescription, new Rgb(255, 255, 0), saved.NeutralColor, palette, OnChoiceChanged),
        ];
        // Scale 1 is the game's own size, the same as "as in the game".
        double? creepScale = saved.CreepScale == 1 ? null : saved.CreepScale;
        CreepScales = Options(GameConfig.CreepScales, creepScale,
            s => string.Format(CultureInfo.CurrentCulture, Strings.ConfigTimesFormat, s.ToString("0.##", CultureInfo.CurrentCulture)));
        _creepScale = CreepScales.First(o => o.Value == creepScale);
        MenuFpsLimits = Options(GameConfig.MenuFpsLimits.Select(f => (double)f), saved.MenuFpsLimit,
            f => f.ToString("0", CultureInfo.CurrentCulture));
        _menuFps = MenuFpsLimits.First(o => o.Value == saved.MenuFpsLimit);
        _hideFrontpageVideo = saved.HideFrontpageVideo;
        _noParticleLights = saved.NoParticleLights;
        _minimalism = saved.Minimalism;
        Backup = new BackupBlockViewModel(services, RunAsync);
        Launch = new LaunchBlockViewModel(services, RunAsync);
        Transfer = new TransferBlockViewModel(services, RunAsync);
        Presets = new PresetBlockViewModel(services, RunAsync, CurrentPresetChoice, ApplyPreset);
    }

    public BackupBlockViewModel Backup { get; }

    public LaunchBlockViewModel Launch { get; }

    public TransferBlockViewModel Transfer { get; }

    public PresetBlockViewModel Presets { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>Raised after any change of the choice, also one that came from a preset.</summary>
    public event Action? ChoiceChanged;

    public IReadOnlyList<ColorRow> Colors { get; }

    public IReadOnlyList<NumberOption> CreepScales { get; }

    public IReadOnlyList<NumberOption> MenuFpsLimits { get; }

    [ObservableProperty]
    private NumberOption _creepScale;

    [ObservableProperty]
    private NumberOption _menuFps;

    [ObservableProperty]
    private bool _hideFrontpageVideo;

    [ObservableProperty]
    private bool _noParticleLights;

    /// <summary>The minimalism preset; the minimap colours stay as chosen on the colours page.</summary>
    [ObservableProperty]
    private bool _minimalism;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(RemoveCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isOk;

    [ObservableProperty]
    private bool _isAttention;

    [ObservableProperty]
    private string? _note;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private bool _canWriteNow;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool _canRemoveNow;

    private bool CanWrite => CanWriteNow && !IsBusy;

    private bool CanRemove => CanRemoveNow && !IsBusy;

    public void Activated()
    {
        if (!IsBusy)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// Puts a choice from elsewhere (a preset) on the page; it is saved as any change and
    /// reaches the game with the write button. Values come snapped to the lists.
    /// </summary>
    internal void SetChoice(GameConfig config)
    {
        GameConfig snapped = config.Snapped();
        Colors[0].Set(snapped.FriendlyColor);
        Colors[1].Set(snapped.EnemyColor);
        Colors[2].Set(snapped.NeutralColor);
        CreepScale = CreepScales.First(o => o.Value == snapped.CreepScale);
        MenuFps = MenuFpsLimits.First(o => o.Value == snapped.MenuFpsLimit);
        HideFrontpageVideo = snapped.HideFrontpageVideo;
        NoParticleLights = snapped.NoParticleLights;
        Minimalism = snapped.Minimalism;
    }

    /// <summary>The choice on the page as it is saved and written.</summary>
    public GameConfig Choice() => new()
    {
        FriendlyColor = Colors[0].Value,
        EnemyColor = Colors[1].Value,
        NeutralColor = Colors[2].Value,
        CreepScale = CreepScale.Value,
        HideFrontpageVideo = HideFrontpageVideo,
        MenuFpsLimit = MenuFps.Value is double fps ? (int)fps : null,
        NoParticleLights = NoParticleLights,
        Minimalism = Minimalism,
    };

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task WriteAsync() => Change(() => _services.Autoexec.Apply(_services.ReadGame(), Choice().Lines()));

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveAsync() => Change(() => _services.Autoexec.Remove(_services.ReadGame()));

    partial void OnCreepScaleChanged(NumberOption value) => OnChoiceChanged();

    partial void OnMenuFpsChanged(NumberOption value) => OnChoiceChanged();

    partial void OnHideFrontpageVideoChanged(bool value) => OnChoiceChanged();

    partial void OnNoParticleLightsChanged(bool value) => OnChoiceChanged();

    partial void OnMinimalismChanged(bool value) => OnChoiceChanged();

    partial void OnIsBusyChanged(bool value)
    {
        Backup.IsBusy = value;
        Launch.IsBusy = value;
        Transfer.IsBusy = value;
        Presets.IsBusy = value;
    }

    /// <summary>What a preset exported now carries: saved effect groups, this page and the launch switches.</summary>
    private PresetChoice CurrentPresetChoice() =>
        PresetReview.Choice(_services.Settings.Load().EffectGroups, Choice(), _game?.LaunchOptions);

    /// <summary>
    /// The chosen lines of a loaded preset: effect groups are saved for the effects page and the
    /// next pack, the config goes on this page and the launch options onto their switches;
    /// writing stays with the buttons.
    /// </summary>
    private void ApplyPreset(PresetReview review, PresetChoiceParts parts)
    {
        if (parts.Effects)
        {
            try
            {
                _services.Settings.Save(_services.Settings.Load() with
                {
                    EffectGroups = review.Effects is { } groups && groups.Count == EffectRules.All.Count ? null : review.Effects,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _services.Log.Error("Could not save the effect groups of a preset", ex);
            }
        }

        if (parts.Config)
        {
            SetChoice(review.Config ?? new GameConfig());
        }

        if (parts.Launch)
        {
            Launch.NoVideo = review.Launch.Contains(LaunchOptionsEditor.NoVideo);
            Launch.NoJoystick = review.Launch.Contains(LaunchOptionsEditor.NoJoystick);
        }
    }

    private async Task Change(Func<ConfigRefusal?> change)
    {
        ConfigRefusal? refusal = null;
        string? error = await RunAsync(() =>
        {
            refusal = change();
            return null;
        }, Strings.ConfigErrorFormat);
        Note = error ?? RefusalText(refusal, _game) ?? Note;
    }

    /// <summary>Runs a change of the game off the UI thread, then reads the page again; returns the change's message or the error.</summary>
    private async Task<string?> RunAsync(Func<string?> change, string errorFormat)
    {
        IsBusy = true;
        string? message;
        try
        {
            message = await Task.Run(change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or KeyValuesException)
        {
            _services.Log.Error("Change on the config page failed", ex);
            message = string.Format(CultureInfo.CurrentCulture, errorFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
        return message;
    }

    internal static string? RefusalText(ConfigRefusal? refusal, GameState? game) => refusal switch
    {
        ConfigRefusal.DotaRunning => Strings.ConfigDotaRunning,
        ConfigRefusal.NoGame => Strings.ProblemDotaNotFound,
        ConfigRefusal.BrokenMarks => string.Format(CultureInfo.CurrentCulture, Strings.ConfigBrokenMarksFormat,
            game?.Dota is { } dota ? AutoexecService.PathIn(dota.GameRoot) : "autoexec.cfg"),
        _ => null,
    };

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            PageState state = await Task.Run(ReadState);
            (_game, _written, _health) = (state.Game, state.Written, state.Health);
            Note = BaseNote;
            Show();
            Backup.Update(_game);
            Launch.Update(_game, state.Launch);
            Transfer.Update(_game, state.Accounts, state.Restorable);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private PageState ReadState()
    {
        GameState game = _services.ReadGame();
        LaunchChange? launch = null;
        try
        {
            launch = game.Steam?.Account is { } account ? _services.Launch.ChangeOf(account.AccountId) : null;
        }
        catch (InvalidDataException ex)
        {
            // An unreadable launch.json is logged by its store; "put back" is then not offered.
            _services.Log.Error("Launch state is unreadable on the config page", ex);
        }

        IReadOnlyList<GameAccount> accounts = [];
        var restorable = new HashSet<uint>();
        try
        {
            accounts = TransferService.Accounts(game);
            restorable.UnionWith(_services.Transfer.Restorable());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or KeyValuesException)
        {
            // Without the accounts the transfer block says there is only one.
            _services.Log.Error("Accounts or transfer state are unreadable on the config page", ex);
        }

        try
        {
            return new PageState(game, _services.Autoexec.Current, _services.Autoexec.Check(), launch, accounts, restorable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // An unreadable autoexec.json is logged by its store; the page shows nothing written.
            _services.Log.Error("autoexec state is unreadable on the config page", ex);
            return new PageState(game, null, AutoexecHealth.NotWritten, launch, accounts, restorable);
        }
    }

    private sealed record PageState(
        GameState Game,
        AutoexecState? Written,
        AutoexecHealth Health,
        LaunchChange? Launch,
        IReadOnlyList<GameAccount> Accounts,
        IReadOnlySet<uint> Restorable);

    private void OnChoiceChanged()
    {
        Show();
        try
        {
            _services.Settings.Save(_services.Settings.Load() with { Config = Choice() });
            Note = BaseNote;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Error("Could not save the config choice", ex);
            Note = string.Format(CultureInfo.CurrentCulture, Strings.ConfigSaveErrorFormat, ex.Message);
        }

        ChoiceChanged?.Invoke();
    }

    /// <summary>Shows how autoexec.cfg relates to the choice; the note is left to the caller.</summary>
    private void Show()
    {
        if (_game is null)
        {
            return;
        }

        if (_game.Dota is null)
        {
            Summary = Strings.ProblemDotaNotFound;
            (IsOk, IsAttention, CanWriteNow, CanRemoveNow) = (false, true, false, false);
            return;
        }

        IReadOnlyList<ConfigLine> lines = Choice().Lines();
        bool open = !_game.DotaRunning;
        if (_written is null)
        {
            Summary = lines.Count == 0 ? Strings.ConfigNothingWritten : Strings.ConfigNotWrittenYet;
            (IsOk, IsAttention) = (false, false);
            CanWriteNow = open && lines.Count > 0;
            CanRemoveNow = false;
            return;
        }

        bool matches = _health == AutoexecHealth.Ready && _written.Lines.SequenceEqual(lines);
        Summary = _health switch
        {
            AutoexecHealth.Missing => Strings.ConfigMissing,
            AutoexecHealth.Changed => Strings.ConfigChanged,
            _ => matches ? Strings.ConfigWritten : Strings.ConfigDiffers,
        };
        (IsOk, IsAttention) = (matches, !matches);
        CanWriteNow = open && !matches && lines.Count > 0;
        CanRemoveNow = open;
    }

    private string? BaseNote => _game?.DotaRunning == true ? Strings.ConfigDotaRunning : null;

    /// <summary>"As in the game", the offered values, and a saved value that is not among them (from a set).</summary>
    private static List<NumberOption> Options(IEnumerable<double> offered, double? saved, Func<double, string> text)
    {
        List<double> values = [.. offered];
        if (saved is double extra && !values.Contains(extra))
        {
            values.Add(extra);
            values.Sort();
        }

        return [new NumberOption(null, Strings.ConfigAsInGame), .. values.Select(v => new NumberOption(v, text(v)))];
    }

    /// <summary>The game's own colours first, then bright ones that stand out on the dark minimap.</summary>
    private static List<Swatch> Palette() =>
    [
        new(new Rgb(0, 255, 0), Strings.ColorGreen),
        new(new Rgb(255, 0, 0), Strings.ColorRed),
        new(new Rgb(255, 255, 255), Strings.ColorWhite),
        new(new Rgb(0, 255, 255), Strings.ColorCyan),
        new(new Rgb(255, 0, 255), Strings.ColorMagenta),
        new(new Rgb(255, 255, 0), Strings.ColorYellow),
        new(new Rgb(33, 161, 255), Strings.ColorBlue),
        new(new Rgb(255, 140, 0), Strings.ColorOrange),
        new(new Rgb(160, 96, 255), Strings.ColorPurple),
    ];
}
