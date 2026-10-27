using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Config;
using DotaTune.Core.Game;
using DotaTune.Core.Presets;
using DotaTune.Resources;

namespace DotaTune.ViewModels;

/// <summary>One part of a loaded settings copy, with its switch.</summary>
public sealed partial class BackupPartRow(BackupPart part, string title, string description, bool isChecked, Action changed) : ObservableObject
{
    public BackupPart Part { get; } = part;

    public string Title { get; } = title;

    public string Description { get; } = description;

    [ObservableProperty]
    private bool _isChecked = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>
/// A copy of the signed-in account's own settings on the "Config and launch" page: saved to a
/// file in one go, put back part by part after a look at what it holds, and the last restore
/// undone within the session.
/// </summary>
public sealed partial class BackupBlockViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<Func<string?>, string, Task<string?>> _run;
    private GameState? _game;

    /// <param name="run">Runs a change off the UI thread, reads the page again and returns the
    /// change's message, or the error put into the given format.</param>
    internal BackupBlockViewModel(AppServices services, Func<Func<string?>, string, Task<string?>> run)
    {
        _services = services;
        _run = run;
        services.Backup.ForgetUndo();
    }

    /// <summary>Asks where to save, offering the given file name; set by the view. Null when cancelled.</summary>
    public Func<string, Task<string?>>? PickSavePath { get; set; }

    /// <summary>Asks which copy to open; set by the view. Null when cancelled.</summary>
    public Func<Task<string?>>? PickOpenPath { get; set; }

    /// <summary>The copy saved last, while its file is still there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastCopy))]
    [NotifyCanExecuteChangedFor(nameof(RestoreLastCommand))]
    private string? _lastCopy;

    [ObservableProperty]
    private string? _lastCopyText;

    /// <summary>A loaded copy waiting for "restore".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private SettingsBackup? _pending;

    [ObservableProperty]
    private string? _pendingTitle;

    [ObservableProperty]
    private IReadOnlyList<BackupPartRow> _rows = [];

    /// <summary>While Steam is running and the launch line changes, the button says Steam is restarted.</summary>
    [ObservableProperty]
    private string _applyText = Strings.BackupApply;

    [ObservableProperty]
    private string? _note;

    /// <summary>Shown after a restore into an account with Steam Cloud on.</summary>
    [ObservableProperty]
    private string? _cloudHint;

    /// <summary>A restore of this session can be undone.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    /// <summary>The page is changing or reading the game; set by the page.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RestoreLastCommand), nameof(OpenFileCommand), nameof(ApplyCommand), nameof(UndoCommand))]
    private bool _isBusy;

    public bool HasPending => Pending is not null;

    public bool HasLastCopy => LastCopy is not null;

    private bool NotBusy => !IsBusy;

    private bool CanRestoreLast => LastCopy is not null && !IsBusy;

    private bool CanApply => Pending is not null && !IsBusy && Rows.Any(r => r.IsChecked);

    private bool CanUndoNow => CanUndo && !IsBusy;

    /// <summary>The game as read and the last copy, if its file is still there.</summary>
    internal void Update(GameState game)
    {
        _game = game;
        string? last = null;
        try
        {
            last = _services.Settings.Load().LastBackup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Error("Settings are unreadable for the last settings copy", ex);
        }

        LastCopy = last is not null && File.Exists(last) ? last : null;
        LastCopyText = LastCopy is null ? null : string.Format(CultureInfo.CurrentCulture, Strings.BackupLastFormat,
            Path.GetFileName(LastCopy), File.GetLastWriteTime(LastCopy).ToString("g", CultureInfo.CurrentCulture));
        ShowApplyText();
    }

    /// <summary>Saves the copy of the signed-in account and remembers it as the last one.</summary>
    internal async Task SaveToAsync(string path)
    {
        bool saved = false;
        Note = await _run(() =>
        {
            SettingsBackup? backup = _services.Backup.Take(_services.ReadGame(), AppServices.AppVersion);
            if (backup is null)
            {
                return Strings.ProblemNoAccount;
            }

            if (backup.Parts.Count == 0)
            {
                return Strings.BackupNothing;
            }

            SettingsBackupFile.Write(path, backup);
            saved = true;
            return string.Format(CultureInfo.CurrentCulture, Strings.BackupSavedFormat, Path.GetFileName(path),
                string.Join(", ", backup.Parts.Select(p => Title(p).ToLower(CultureInfo.CurrentCulture))));
        }, Strings.BackupErrorFormat);

        if (!saved)
        {
            return;
        }

        // On the UI thread, as every other save of the settings, so none of them is lost.
        try
        {
            _services.Settings.Save(_services.Settings.Load() with { LastBackup = path });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The copy is saved; only the button for it is missing until the next save.
            _services.Log.Error("Could not remember the last settings copy", ex);
        }

        if (_game is not null)
        {
            Update(_game);
        }
    }

    /// <summary>Reads a copy and shows what it holds.</summary>
    internal async Task OpenFromAsync(string path)
    {
        try
        {
            Show(await Task.Run(() => SettingsBackupFile.Read(path)));
        }
        catch (Exception ex) when (ex is PresetException or IOException or UnauthorizedAccessException)
        {
            _services.Log.Error($"Settings copy {path} was not loaded", ex);
            Note = ProblemText(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task SaveAsync()
    {
        if (PickSavePath is not null && await PickSavePath(SuggestedName()) is string path)
        {
            await SaveToAsync(path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestoreLast))]
    private Task RestoreLastAsync() => OpenFromAsync(LastCopy!);

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task OpenFileAsync()
    {
        if (PickOpenPath is not null && await PickOpenPath() is string path)
        {
            await OpenFromAsync(path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        SettingsBackup backup = Pending!;
        BackupPart[] parts = [.. Rows.Where(r => r.IsChecked).Select(r => r.Part)];
        BackupResult? result = await PutAsync(
            game => _services.Backup.Restore(game, backup, parts),
            game => SettingsBackupService.NeedsSteamClosed(game, backup, parts),
            Strings.BackupRestoredFormat,
            ApplyText == Strings.BackupApplyRestart);
        if (result is { Refusal: null })
        {
            Pending = null;
            Rows = [];
            CanUndo |= result.Written > 0;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Pending = null;
        Rows = [];
        Note = null;
    }

    [RelayCommand(CanExecute = nameof(CanUndoNow))]
    private async Task UndoAsync()
    {
        BackupResult? result = await PutAsync(_services.Backup.Undo, _services.Backup.UndoNeedsSteamClosed, Strings.BackupUndoneFormat, closesSteam: false);
        if (result is { Refusal: null or BackupRefusal.NothingToUndo })
        {
            CanUndo = false;
        }
    }

    /// <summary>
    /// Runs a restore or its undo on the game as read now, through a Steam restart when the launch
    /// line changes while Steam is running. Null when the run failed with an error.
    /// </summary>
    /// <param name="closesSteam">Steam is known to close for it, so the wait is said first.</param>
    private async Task<BackupResult?> PutAsync(Func<GameState, BackupResult> put, Func<GameState, bool> needsSteamClosed, string doneFormat, bool closesSteam)
    {
        BackupResult? result = null;
        string? cloudAccount = null;
        CloudHint = null;
        if (closesSteam)
        {
            Note = Strings.LaunchSteamClosing;
        }

        Note = await _run(() =>
        {
            GameState game = _services.ReadGame();
            return needsSteamClosed(game) && game.SteamRunning
                ? LaunchBlockViewModel.WithSteamClosed(_services, Change, Strings.BackupDotaRunning)
                : Change(game);
        }, Strings.BackupErrorFormat);
        CloudHint = cloudAccount is null ? null : string.Format(CultureInfo.CurrentCulture, Strings.TransferCloudHintFormat, cloudAccount);
        return result;

        string Change(GameState game)
        {
            result = put(game);
            if (result.Refusal is null && result.Written > 0 && game.Steam is { Account: { } account } steam
                && TransferService.CloudEnabled(steam.Root, account.AccountId))
            {
                cloudAccount = account.AccountName;
            }

            return ResultText(result, doneFormat, game);
        }
    }

    private void Show(SettingsBackup backup)
    {
        CloudHint = null;
        Note = null;
        string date = backup.Manifest.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        PendingTitle = backup.Manifest.Account is { } account
            ? string.Format(CultureInfo.CurrentCulture, Strings.BackupFromFormat, account, date)
            : string.Format(CultureInfo.CurrentCulture, Strings.BackupFromDateFormat, date);

        // Graphics of another computer carry its resolution and monitor, so they wait for a tick.
        Rows = [.. backup.Parts.Select(part => new BackupPartRow(
            part, Title(part), Description(backup, part), part != BackupPart.Graphics || backup.FromThisComputer, OnRowChanged))];
        Pending = backup;
        ShowApplyText();
    }

    private void OnRowChanged()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        ShowApplyText();
    }

    private void ShowApplyText() =>
        ApplyText = _game is { SteamRunning: true } && Pending is { } pending
            && SettingsBackupService.NeedsSteamClosed(_game, pending, [.. Rows.Where(r => r.IsChecked).Select(r => r.Part)])
            ? Strings.BackupApplyRestart
            : Strings.BackupApply;

    /// <summary>"Dota settings login 2026-10-27"; a login is plain ASCII, anything else is replaced.</summary>
    private string SuggestedName()
    {
        string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string? login = _game?.Steam?.Account?.AccountName;
        string who = string.IsNullOrWhiteSpace(login)
            ? date
            : string.Concat(login.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + " " + date;
        return string.Format(CultureInfo.CurrentCulture, Strings.BackupFileNameFormat, who) + SettingsBackupFile.Extension;
    }

    private static string Title(BackupPart part) => part switch
    {
        BackupPart.Graphics => Strings.TransferGraphics,
        BackupPart.Hotkeys => Strings.TransferHotkeys,
        BackupPart.ConsoleBinds => Strings.TransferConsoleBinds,
        BackupPart.GameSettings => Strings.TransferGameSettings,
        BackupPart.LaunchOptions => Strings.TransferLaunchOptions,
        BackupPart.Autoexec => Strings.BackupAutoexec,
        BackupPart.HeroGrids => Strings.TransferHeroGrids,
        BackupPart.Builds => Strings.TransferBuilds,
        BackupPart.Misc => Strings.TransferMisc,
        _ => part.ToString(),
    };

    private static string Description(SettingsBackup backup, BackupPart part) => part switch
    {
        BackupPart.Graphics => backup.FromThisComputer ? Strings.TransferGraphicsDescription : Strings.BackupGraphicsOther,
        BackupPart.Hotkeys => Strings.TransferHotkeysDescription,
        BackupPart.ConsoleBinds => Strings.TransferConsoleBindsDescription,
        BackupPart.GameSettings => Strings.TransferGameSettingsDescription,
        BackupPart.LaunchOptions => backup.Manifest.LaunchOptions is { Length: > 0 } line ? line : Strings.BackupLaunchEmpty,
        BackupPart.Autoexec => string.Format(CultureInfo.CurrentCulture, Strings.BackupAutoexecFormat, LineCount(backup.Autoexec)),
        BackupPart.HeroGrids => Strings.BackupHeroGridsDescription,
        BackupPart.Builds => Strings.TransferBuildsDescription,
        BackupPart.Misc => Strings.TransferMiscDescription,
        _ => string.Empty,
    };

    private static int LineCount(byte[]? text) =>
        text is null ? 0 : AutoexecText.Encoding.GetString(text).Split('\n').Count(l => l.Trim().Length > 0);

    private static string ResultText(BackupResult result, string doneFormat, GameState game)
    {
        string? refusal = result.Refusal switch
        {
            null => null,
            BackupRefusal.DotaRunning => Strings.BackupDotaRunning,
            BackupRefusal.SteamRunning => Strings.BackupSteamRunning,
            BackupRefusal.NoAccount => Strings.ProblemNoAccount,
            BackupRefusal.NoGameFolder => Strings.BackupNoGameFolder,
            BackupRefusal.BrokenMarks => string.Format(CultureInfo.CurrentCulture, Strings.ConfigBrokenMarksFormat,
                game.Dota is { } dota ? AutoexecService.PathIn(dota.GameRoot) : SettingsBackupFile.AutoexecName),
            BackupRefusal.NothingToUndo => Strings.BackupNothingToUndo,
            _ => Strings.BackupNothingChosen,
        };
        if (refusal is not null)
        {
            return refusal;
        }

        string text = result.Written == 0 ? Strings.BackupAlreadySame : string.Format(CultureInfo.CurrentCulture, doneFormat, result.Written);
        if (result.GridsSkipped)
        {
            text += " " + Strings.BackupGridsNotSeen;
        }

        if (result.LaunchSkipped)
        {
            text += " " + Strings.LaunchNoDotaBlock;
        }

        return text;
    }

    private static string ProblemText(Exception ex) => ex switch
    {
        PresetException { Problem: PresetProblem.Newer } => Strings.BackupNewer,
        PresetException { Problem: PresetProblem.Damaged } => Strings.BackupDamaged,
        PresetException { Problem: PresetProblem.TooLarge } => Strings.BackupTooLarge,
        PresetException => Strings.BackupNotACopy,
        _ => string.Format(CultureInfo.CurrentCulture, Strings.BackupErrorFormat, ex.Message),
    };
}
