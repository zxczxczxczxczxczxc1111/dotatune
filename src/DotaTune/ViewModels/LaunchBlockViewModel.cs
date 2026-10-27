using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Config;
using DotaTune.Core.Game;
using DotaTune.Core.Steam;
using DotaTune.Resources;

namespace DotaTune.ViewModels;

/// <summary>
/// Launch options of the signed-in account on the "Config and launch" page: -novid, -nojoy and
/// the options the game no longer has. The switches show the line as it is; a change goes to
/// Steam only with the button, and a running Steam is restarted for it.
/// </summary>
public sealed partial class LaunchBlockViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<Func<string?>, string, Task<string?>> _run;
    private string? _line;
    private bool _steamRunning;
    private bool _dotaRunning;
    private bool _syncing;

    /// <param name="run">Runs a change off the UI thread, reads the page again and returns the
    /// change's message, or the error put into the given format.</param>
    internal LaunchBlockViewModel(AppServices services, Func<Func<string?>, string, Task<string?>> run)
    {
        _services = services;
        _run = run;
    }

    [ObservableProperty]
    private string _current = string.Empty;

    /// <summary>The line after the button; null while nothing would change.</summary>
    [ObservableProperty]
    private string? _result;

    [ObservableProperty]
    private bool _noVideo;

    [ObservableProperty]
    private bool _noJoystick;

    [ObservableProperty]
    private bool _consoleLog;

    [ObservableProperty]
    private bool _removeDead = true;

    /// <summary>Dead options of the line, for the switch that takes them out; null when there are none.</summary>
    [ObservableProperty]
    private string? _deadOptions;

    /// <summary>The switches make sense: there is an account with a Dota line.</summary>
    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private string? _note;

    /// <summary>While Steam is running the buttons say that it is restarted for the change.</summary>
    [ObservableProperty]
    private string _writeText = Strings.LaunchWrite;

    [ObservableProperty]
    private string _restoreText = Strings.LaunchRestore;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private bool _canWriteNow;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private bool _canRestoreNow;

    /// <summary>The page is changing or reading the game; set by the page.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(RestoreCommand))]
    private bool _isBusy;

    private bool CanWrite => CanWriteNow && !IsBusy;

    private bool CanRestore => CanRestoreNow && !IsBusy;

    /// <summary>The line DotaTune would write now.</summary>
    public string Planned => LaunchOptionsEditor.Plan(_line, NoVideo, NoJoystick, RemoveDead, ConsoleLog);

    /// <summary>Shows the line of the account as read; the switches follow it.</summary>
    internal void Update(GameState game, LaunchChange? change)
    {
        _line = game.LaunchOptions;
        _steamRunning = game.SteamRunning;
        _dotaRunning = game.DotaRunning;
        IReadOnlyList<LaunchOption> options = LaunchOptionsEditor.Parse(_line);
        _syncing = true;
        try
        {
            NoVideo = LaunchOptionsEditor.Has(options, LaunchOptionsEditor.NoVideo);
            NoJoystick = LaunchOptionsEditor.Has(options, LaunchOptionsEditor.NoJoystick);
            ConsoleLog = LaunchOptionsEditor.Has(options, LaunchOptionsEditor.ConsoleLog);
            RemoveDead = true;
        }
        finally
        {
            _syncing = false;
        }

        List<LaunchOption> dead = [.. options.Where(o => LaunchOptionsEditor.DeadOptions.Contains(o.Name))];
        DeadOptions = dead.Count == 0 ? null : string.Join(", ", dead);
        IsAvailable = game.Steam?.Account is not null && _line is not null;
        Current = !IsAvailable ? Strings.ProblemNoAccount
            : _line!.Length == 0 ? Strings.LaunchCurrentNone
            : string.Format(CultureInfo.CurrentCulture, Strings.LaunchCurrentFormat, _line);
        Note = game.DotaRunning && IsAvailable ? Strings.LaunchDotaRunning : null;
        WriteText = game.SteamRunning ? Strings.LaunchRestartAndWrite : Strings.LaunchWrite;
        RestoreText = game.SteamRunning ? Strings.LaunchRestartAndRestore : Strings.LaunchRestore;
        CanRestoreNow = change is not null && !game.DotaRunning;
        ShowPlan();
    }

    partial void OnNoVideoChanged(bool value) => OnSwitched();

    partial void OnNoJoystickChanged(bool value) => OnSwitched();

    partial void OnConsoleLogChanged(bool value) => OnSwitched();

    partial void OnRemoveDeadChanged(bool value) => OnSwitched();

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        string planned = Planned;
        Note = _steamRunning ? Strings.LaunchSteamClosing : Note;
        Note = await _run(() => WithSteamClosed(game => RefusalText(_services.Launch.Apply(game, planned)) ?? Strings.LaunchWritten), Strings.LaunchErrorFormat);
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        Note = _steamRunning ? Strings.LaunchSteamClosing : Note;
        Note = await _run(() => WithSteamClosed(game => RefusalText(_services.Launch.Restore(game)) ?? Strings.LaunchRestored), Strings.LaunchErrorFormat);
    }

    /// <summary>
    /// Makes the change on the game as read now. A running Steam is closed for it and started
    /// again, and the change goes only to the account that was signed in before.
    /// </summary>
    private string WithSteamClosed(Func<GameState, string> change)
    {
        GameState game = _services.ReadGame();
        if (!game.SteamRunning)
        {
            return change(game);
        }

        uint? account = game.Steam?.Account?.AccountId;
        SteamRestartResult<string> restart = _services.SteamRestart.Around(game, () =>
        {
            GameState closed = _services.ReadGame();
            return closed.Steam?.Account?.AccountId == account ? change(closed) : Strings.ProblemNoAccount;
        });
        return restart.Problem switch
        {
            null => $"{restart.Result} {Strings.LaunchSteamStarting}",
            SteamRestartProblem.DidNotStart => $"{restart.Result} {Strings.LaunchSteamDidNotStart}",
            SteamRestartProblem.DidNotClose => Strings.LaunchSteamDidNotClose,
            SteamRestartProblem.DotaRunning => Strings.LaunchDotaRunning,
            _ => Strings.ProblemSteamNotFound,
        };
    }

    private void OnSwitched()
    {
        if (!_syncing)
        {
            ShowPlan();
        }
    }

    private void ShowPlan()
    {
        string planned = Planned;
        bool changes = IsAvailable && !string.Equals(planned, _line, StringComparison.Ordinal);
        Result = !changes ? null
            : planned.Length == 0 ? Strings.LaunchResultNone
            : string.Format(CultureInfo.CurrentCulture, Strings.LaunchResultFormat, planned);
        CanWriteNow = changes && !_dotaRunning;
    }

    private static string? RefusalText(LaunchRefusal? refusal) => refusal switch
    {
        LaunchRefusal.SteamRunning => Strings.LaunchSteamRunning,
        LaunchRefusal.NoAccount => Strings.ProblemNoAccount,
        LaunchRefusal.NoDotaBlock => Strings.LaunchNoDotaBlock,
        LaunchRefusal.ChangedInSteam => Strings.LaunchChangedInSteam,
        _ => null,
    };
}
