using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Config;
using DotaTune.Core.Game;
using DotaTune.Resources;

namespace DotaTune.ViewModels;

/// <summary>An account in the "from" and "to" lists.</summary>
public sealed record AccountOption(GameAccount Account, string Text)
{
    public override string ToString() => Text;
}

/// <summary>One group of files to carry, with its tick.</summary>
public sealed partial class TransferGroupRow(TransferGroup group, string title, string description, bool isChecked, Action changed) : ObservableObject
{
    public TransferGroup Group { get; } = group;

    public string Title { get; } = title;

    public string Description { get; } = description;

    [ObservableProperty]
    private bool _isChecked = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>
/// Transfer of game settings between the accounts of this computer on the "Config and launch"
/// page. Everything it replaces is copied first and can be put back.
/// </summary>
public sealed partial class TransferBlockViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<Func<string?>, string, Task<string?>> _run;
    private GameState? _game;
    private IReadOnlySet<uint> _restorable = new HashSet<uint>();

    // The last run went through and the choice is the same: its button would read as "not done yet".
    private bool _done;

    /// <param name="run">Runs a change off the UI thread, reads the page again and returns the
    /// change's message, or the error put into the given format.</param>
    internal TransferBlockViewModel(AppServices services, Func<Func<string?>, string, Task<string?>> run)
    {
        _services = services;
        _run = run;
        Groups =
        [
            Row(TransferGroup.Hotkeys, Strings.TransferHotkeys, Strings.TransferHotkeysDescription),
            Row(TransferGroup.GameSettings, Strings.TransferGameSettings, Strings.TransferGameSettingsDescription),
            Row(TransferGroup.ConsoleBinds, Strings.TransferConsoleBinds, Strings.TransferConsoleBindsDescription),
            Row(TransferGroup.HeroGrids, Strings.TransferHeroGrids, Strings.TransferHeroGridsDescription),
            Row(TransferGroup.Builds, Strings.TransferBuilds, Strings.TransferBuildsDescription),
            Row(TransferGroup.Misc, Strings.TransferMisc, Strings.TransferMiscDescription),
            Row(TransferGroup.Graphics, Strings.TransferGraphics, Strings.TransferGraphicsDescription),
            Row(TransferGroup.LaunchOptions, Strings.TransferLaunchOptions, Strings.TransferLaunchOptionsDescription, isChecked: false),
        ];
    }

    public IReadOnlyList<TransferGroupRow> Groups { get; }

    [ObservableProperty]
    private IReadOnlyList<AccountOption> _accounts = [];

    [ObservableProperty]
    private AccountOption? _source;

    [ObservableProperty]
    private AccountOption? _target;

    /// <summary>Two or more accounts have started Dota on this computer.</summary>
    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private string? _note;

    /// <summary>Shown after a transfer into an account with Steam Cloud on.</summary>
    [ObservableProperty]
    private string? _cloudHint;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TransferCommand))]
    private bool _canTransferNow;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private bool _canRestoreNow;

    /// <summary>The page is changing or reading the game; set by the page.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TransferCommand), nameof(RestoreCommand))]
    private bool _isBusy;

    private bool CanTransfer => CanTransferNow && !IsBusy;

    private bool CanRestore => CanRestoreNow && !IsBusy;

    /// <summary>The accounts as read; a choice made before stays while its account is still there.</summary>
    internal void Update(GameState game, IReadOnlyList<GameAccount> accounts, IReadOnlySet<uint> restorable)
    {
        _game = game;
        _restorable = restorable;
        _done = false;
        uint? source = Source?.Account.AccountId;
        uint? target = Target?.Account.AccountId;
        uint? signedIn = game.Steam?.Account?.AccountId;
        Accounts = [.. accounts.Select(a => new AccountOption(a, a.Name.Length == 0
            ? a.AccountId.ToString(CultureInfo.CurrentCulture)
            : string.Format(CultureInfo.CurrentCulture, Strings.TransferAccountFormat, a.Name, a.AccountId)))];
        IsAvailable = Accounts.Count > 1;
        Target = Find(target) ?? Find(signedIn) ?? Accounts.LastOrDefault();
        Source = Find(source) ?? Accounts.FirstOrDefault(a => a != Target);
        Note = !IsAvailable ? Strings.TransferOneAccount : null;
        Refresh();

        AccountOption? Find(uint? id) => Accounts.FirstOrDefault(a => a.Account.AccountId == id);
    }

    partial void OnSourceChanged(AccountOption? value) => OnChoiceChanged();

    partial void OnTargetChanged(AccountOption? value) => OnChoiceChanged();

    [RelayCommand(CanExecute = nameof(CanTransfer))]
    private async Task TransferAsync()
    {
        GameAccount source = Source!.Account;
        GameAccount target = Target!.Account;
        string targetText = Target.Text;
        TransferGroup[] groups = [.. Groups.Where(g => g.IsChecked).Select(g => g.Group)];
        bool done = false;
        Note = await _run(() =>
        {
            TransferResult result = _services.Transfer.Apply(_services.ReadGame(), source.AccountId, target.AccountId, groups);
            done = result.Refusal is null;
            return RefusalText(result.Refusal)
                ?? WithSkippedGrids(string.Format(CultureInfo.CurrentCulture, Strings.TransferDoneFormat, result.Written), result);
        }, Strings.TransferErrorFormat);
        if (done && target.CloudEnabled)
        {
            CloudHint = string.Format(CultureInfo.CurrentCulture, Strings.TransferCloudHintFormat, targetText);
        }

        if (done)
        {
            _done = true;
            Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        uint target = Target!.Account.AccountId;
        CloudHint = null;
        Note = await _run(() => RefusalText(_services.Transfer.Restore(_services.ReadGame(), target).Refusal) ?? Strings.TransferRestored, Strings.TransferErrorFormat);
    }

    private TransferGroupRow Row(TransferGroup group, string title, string description, bool isChecked = true) =>
        new(group, title, description, isChecked, OnChoiceChanged);

    /// <summary>A new choice: the message and the hint of the last run no longer describe it.</summary>
    private void OnChoiceChanged()
    {
        if (IsAvailable)
        {
            Note = null;
        }

        CloudHint = null;
        _done = false;
        Refresh();
    }

    private void Refresh()
    {
        bool dota = _game?.DotaRunning == true;
        bool steamBlocks = _game?.SteamRunning == true && Groups.Any(g => g.Group == TransferGroup.LaunchOptions && g.IsChecked);
        bool chosen = IsAvailable && Source is not null && Target is not null;
        if (chosen && Source == Target)
        {
            Note = Strings.TransferSameAccount;
        }
        else if (chosen && (dota || steamBlocks))
        {
            Note = dota ? Strings.TransferDotaRunning : Strings.TransferSteamRunning;
        }
        else if (Note == Strings.TransferSameAccount || Note == Strings.TransferDotaRunning || Note == Strings.TransferSteamRunning)
        {
            Note = null;
        }

        CanTransferNow = chosen && Source != Target && !dota && !steamBlocks && Groups.Any(g => g.IsChecked) && !_done;
        CanRestoreNow = Target is not null && _restorable.Contains(Target.Account.AccountId) && !dota;
    }

    internal static string? RefusalText(TransferRefusal? refusal) => refusal switch
    {
        TransferRefusal.DotaRunning => Strings.TransferDotaRunning,
        TransferRefusal.SteamRunning => Strings.TransferSteamRunning,
        TransferRefusal.NoSteam => Strings.ProblemSteamNotFound,
        TransferRefusal.SameAccount => Strings.TransferSameAccount,
        TransferRefusal.NoSuchAccount => Strings.TransferNoSuchAccount,
        TransferRefusal.NothingToCopy => Strings.TransferNothing,
        TransferRefusal.GridsNotSeen => Strings.TransferGridsNotSeen,
        TransferRefusal.ChangedSince => Strings.TransferChangedSince,
        TransferRefusal.CopiesLost => Strings.TransferCopiesLost,
        _ => null,
    };

    /// <summary>The message of a done write, with the reason when hero grids were left out.</summary>
    internal static string WithSkippedGrids(string done, TransferResult result) =>
        result.GridsSkipped ? done + " " + Strings.TransferGridsNotSeen : done;
}
