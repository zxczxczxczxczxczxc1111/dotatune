using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Game;
using DotaTune.Core.Mmr;
using DotaTune.Core.Pack;
using DotaTune.Core.Steam;
using DotaTune.Core.Text;
using DotaTune.Resources;

namespace DotaTune.ViewModels;

/// <summary>One reading in the history on the page.</summary>
public sealed class MmrRow
{
    internal MmrRow(MmrReading reading, string? account)
    {
        Time = reading.Time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        Account = account;
        Mmr = reading.Mmr.ToString(CultureInfo.CurrentCulture);
        Change = reading.Delta is { } delta ? ChangeText(delta, reading.Games) : null;
        IsUp = reading.Delta > 0;
        IsDown = reading.Delta < 0;
    }

    public string Time { get; }

    /// <summary>Whose reading it is; null while the history has one account.</summary>
    public string? Account { get; }

    public string Mmr { get; }

    /// <summary>"+25" or "-50 за 2 игры"; null for the first reading of an account.</summary>
    public string? Change { get; }

    public bool IsUp { get; }

    public bool IsDown { get; }

    private static string ChangeText(int delta, int? games)
    {
        string number = delta.ToString("+0;-0;0", CultureInfo.InvariantCulture);
        if (games is not > 1)
        {
            return number;
        }

        string count = string.Format(CultureInfo.CurrentCulture,
            Plural.Choose(CultureInfo.CurrentUICulture, games.Value, Strings.MmrGamesOne, Strings.MmrGamesFew, Strings.MmrGamesMany), games.Value);
        return string.Format(CultureInfo.CurrentCulture, Strings.MmrChangeFormat, number, count);
    }
}

/// <summary>
/// The "MMR" page: the switch of the MMR line, how the installed pack relates to it and the
/// readings taken from the game. A switch is saved at once and reaches the game with the pack.
/// </summary>
public sealed partial class MmrViewModel : ObservableObject, IActivatedPage
{
    /// <summary>Readings on the page, newest first; the file keeps many more.</summary>
    internal const int Shown = 50;

    private readonly AppServices _services;
    private GameState? _game;
    private InstallState? _pack;
    private PackHealth _health;
    private MenuSupport _support = MenuSupport.Unknown;
    private string? _supportBuild;
    private bool _restoring;

    private enum MenuSupport
    {
        Unknown,
        None,
        Line,
    }

    internal MmrViewModel(AppServices services, string title, string description)
    {
        _services = services;
        Title = title;
        Description = description;
        _restoring = true;
        IsOn = services.Settings.Load().MmrLine;
        _restoring = false;
    }

    public string Title { get; }

    public string Description { get; }

    public ObservableCollection<MmrRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConsoleLogHint))]
    private bool _isOn;

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

    [ObservableProperty]
    private string _applyText = Strings.InstallPack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _canApplyNow;

    /// <summary>Instead of the rows: the history is empty or its file is not readable.</summary>
    [ObservableProperty]
    private string? _historyNote;

    /// <summary>The launch line of the account has no -condebug, so a launch from Steam leaves no readings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConsoleLogHint))]
    private bool _needsConsoleLog;

    /// <summary>The hint about -condebug matters only to a player who uses the line.</summary>
    public bool ShowConsoleLogHint => IsOn && NeedsConsoleLog;

    private bool CanApply => CanApplyNow && !IsBusy;

    public void Activated()
    {
        if (!IsBusy)
        {
            _ = RefreshAsync();
        }
    }

    partial void OnIsOnChanged(bool value)
    {
        if (_restoring)
        {
            return;
        }

        try
        {
            _services.Settings.Save(_services.Settings.Load() with { MmrLine = value });
            Note = BaseNote;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Error("Could not save the MMR line switch", ex);
            Note = string.Format(CultureInfo.CurrentCulture, Strings.EffectsSaveErrorFormat, ex.Message);
        }

        Show();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        IsBusy = true;
        bool wanted = IsOn;
        string? message;
        try
        {
            message = GameStateViewModel.RefusalText(await Task.Run(_services.InstallPack));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _services.Log.Error("Pack rebuild for the MMR line failed", ex);
            message = string.Format(CultureInfo.CurrentCulture, Strings.PackErrorFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
        if (message is null && _pack is not null && wanted && !_pack.Mmr)
        {
            message = Strings.MmrNotApplied;
        }

        Note = message ?? Note;
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            (_game, _pack, _health, _support, _supportBuild, IReadOnlyList<MmrRow>? rows, bool needsLog) = await Task.Run(Read);
            Rows.Clear();
            foreach (MmrRow row in rows ?? [])
            {
                Rows.Add(row);
            }

            HistoryNote = rows is null ? Strings.MmrHistoryUnreadable : rows.Count == 0 ? Strings.MmrHistoryEmpty : null;
            NeedsConsoleLog = needsLog;
            Note = BaseNote;
            Show();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private (GameState, InstallState?, PackHealth, MenuSupport, string?, IReadOnlyList<MmrRow>?, bool) Read()
    {
        // Readings of the last session first: taking them in may change the data in the pack.
        GameState game = _services.ReadGame();
        _services.RefreshMmr(game);
        InstallState? pack;
        PackHealth health;
        try
        {
            pack = _services.Packs.Current;
            health = pack is null ? PackHealth.NotInstalled : _services.Packs.Check(game);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The game page reports a broken install.json; here the pack is treated as absent.
            _services.Log.Error("Install state is unreadable on the MMR page", ex);
            (pack, health) = (null, PackHealth.NotInstalled);
        }

        (MenuSupport support, string? build) = (_support, _supportBuild);
        if (game.Dota is { } dota && (support == MenuSupport.Unknown || build != dota.Build))
        {
            (support, build) = (Support(dota), dota.Build);
        }

        bool needsLog = game.Steam?.Account is not null && game.LaunchOptions is not null
            && !LaunchOptionsEditor.Has(LaunchOptionsEditor.Parse(game.LaunchOptions), LaunchOptionsEditor.ConsoleLog);
        return (game, pack, health, support, build, ReadRows(game), needsLog);
    }

    /// <summary>The newest readings, or null when mmr.json is not readable.</summary>
    private List<MmrRow>? ReadRows(GameState game)
    {
        IReadOnlyList<MmrReading> readings;
        try
        {
            readings = _services.Mmr.History.Load();
        }
        catch (InvalidDataException)
        {
            // The store has logged it.
            return null;
        }

        IReadOnlyDictionary<uint, string> names = new Dictionary<uint, string>();
        bool several = readings.Select(r => r.Account).Distinct().Skip(1).Any();
        if (several && game.Steam is { } steam)
        {
            try
            {
                names = SteamLocator.AccountNames(steam.Root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyValuesException)
            {
                _services.Log.Warning($"Account names for the MMR history are not readable: {ex.Message}");
            }
        }

        return [.. readings
            .OrderByDescending(r => r.At)
            .Take(Shown)
            .Select(r => new MmrRow(r, several ? AccountText(r.Account, names) : null))];
    }

    private static string AccountText(uint account, IReadOnlyDictionary<uint, string> names) =>
        names.TryGetValue(account, out string? name) && name.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.TransferAccountFormat, name, account)
            : account.ToString(CultureInfo.CurrentCulture);

    /// <summary>Whether this build of the game takes the MMR line; read once per build.</summary>
    private MenuSupport Support(DotaInstall dota)
    {
        if (_services.MenuScriptsProblem(dota) is { } problem)
        {
            _services.Log.Warning($"MMR line does not fit build {dota.Build}: {problem}");
            return MenuSupport.None;
        }

        return MenuSupport.Line;
    }

    /// <summary>Shows how the installed pack relates to the switch; the note is left to the caller.</summary>
    private void Show()
    {
        if (_game is null)
        {
            return;
        }

        // The line can go in only where the game's menu is the one it is made from.
        bool wanted = IsOn && _support != MenuSupport.None;
        bool installable = PackService.RefusalFor(_game) is null;
        if (_pack is null)
        {
            Summary = !IsOn ? Strings.StateNoPack : _support == MenuSupport.None ? Strings.MmrNotSupported : Strings.MmrNoPack;
            (IsOk, IsAttention) = (false, Summary == Strings.MmrNotSupported);
            ApplyText = Strings.InstallPack;
            CanApplyNow = installable && wanted;
            return;
        }

        // A pack that is gone, replaced or from an older build has to be built again whatever the switch.
        bool matches = _health == PackHealth.Ready && _pack.Mmr == wanted;
        Summary = _health switch
        {
            PackHealth.Missing => Strings.HealthMissing,
            PackHealth.Changed => Strings.HealthChanged,
            PackHealth.FolderNotLoaded => Strings.HealthFolderNotLoaded,
            PackHealth.OtherBuild => Strings.HealthOtherBuild,
            _ when IsOn && _support == MenuSupport.None => Strings.MmrNotSupported,
            _ when matches => _pack.Mmr ? Strings.MmrPackWithLine : Strings.MmrPackWithoutLine,
            _ => _pack.Mmr ? Strings.MmrPackBuiltWithLine : Strings.MmrPackBuiltWithoutLine,
        };
        (IsOk, IsAttention) = (matches && _pack.Mmr, !matches || Summary == Strings.MmrNotSupported);
        ApplyText = Strings.RebuildPack;
        CanApplyNow = !matches && installable;
    }

    private string? BaseNote => _game?.DotaRunning == true ? Strings.DotaRunningNote : null;
}
