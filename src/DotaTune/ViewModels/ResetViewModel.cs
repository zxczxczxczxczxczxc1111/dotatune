using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Game;
using DotaTune.Core.Reset;
using DotaTune.Resources;

namespace DotaTune.ViewModels;

/// <summary>
/// The "Reset" page: what DotaTune changed in the game now and one button that takes all of it
/// back. The choices on the other pages stay, so anything can be put in again from there.
/// </summary>
public sealed partial class ResetViewModel : ObservableObject, IActivatedPage
{
    private readonly AppServices _services;

    internal ResetViewModel(AppServices services, string title, string description)
    {
        _services = services;
        Title = title;
        Description = description;
    }

    public string Title { get; }

    public string Description { get; }

    /// <summary>Titles of the changed parts, in the order the reset takes them back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    private IReadOnlyList<string> _rows = [];

    public bool HasRows => Rows.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand), nameof(ConfirmCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isOk;

    [ObservableProperty]
    private bool _isAttention;

    /// <summary>What stayed after the last reset, one part per line.</summary>
    [ObservableProperty]
    private string? _note;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand), nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(ShowReset))]
    private bool _canResetNow;

    /// <summary>The first press asks again: one press takes back several things at once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReset))]
    private bool _isConfirming;

    public bool ShowReset => CanResetNow && !IsConfirming;

    private bool CanReset => CanResetNow && !IsBusy;

    public void Activated()
    {
        if (!IsBusy)
        {
            IsConfirming = false;
            _ = RefreshAsync(null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset() => IsConfirming = true;

    [RelayCommand]
    private void Cancel() => IsConfirming = false;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ConfirmAsync()
    {
        IsConfirming = false;
        IsBusy = true;
        IReadOnlyList<ResetStep> steps;
        try
        {
            steps = await Task.Run(() => _services.Reset.Run(_services.ReadGame));
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync(steps);
    }

    /// <param name="steps">The steps of a reset just made, to tell what stayed; null for a plain look.</param>
    private async Task RefreshAsync(IReadOnlyList<ResetStep>? steps)
    {
        IsBusy = true;
        try
        {
            (GameState game, IReadOnlyList<ResetPart> pending) = await Task.Run(() =>
            {
                GameState read = _services.ReadGame();
                return (read, _services.Reset.Pending(read));
            });
            Show(game, pending, steps);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(GameState game, IReadOnlyList<ResetPart> pending, IReadOnlyList<ResetStep>? steps)
    {
        Rows = [.. pending.Select(PartTitle)];
        CanResetNow = pending.Count > 0 && !game.DotaRunning;
        List<string> stayed = [.. (steps ?? []).Where(s => s.Problem is not null).Select(s => $"{PartTitle(s.Part)}: {ProblemText(s)}")];
        Note = stayed.Count > 0 ? string.Join(Environment.NewLine, stayed) : null;
        if (stayed.Count > 0)
        {
            (Summary, IsOk, IsAttention) = (Strings.ResetPartly, false, true);
        }
        else if (pending.Count == 0)
        {
            (Summary, IsOk, IsAttention) = (Strings.ResetClean, true, false);
        }
        else if (game.DotaRunning)
        {
            (Summary, IsOk, IsAttention) = (Strings.ResetDotaRunning, false, true);
        }
        else
        {
            (Summary, IsOk, IsAttention) = (string.Empty, false, false);
        }
    }

    private static string PartTitle(ResetPart part) => part switch
    {
        ResetPart.Pack => Strings.ResetPartPack,
        ResetPart.Autoexec => Strings.ResetPartAutoexec,
        ResetPart.Transfers => Strings.ResetPartTransfers,
        ResetPart.LaunchOptions => Strings.ResetPartLaunch,
        ResetPart.HeroGrids => Strings.ResetPartGrids,
        _ => throw new InvalidOperationException($"Unknown reset part {part}"),
    };

    private static string ProblemText(ResetStep step) => step.Problem switch
    {
        ResetProblem.DotaRunning => Strings.ResetProblemDota,
        ResetProblem.SteamRunning => Strings.ResetProblemSteam,
        ResetProblem.NoSteam => Strings.ResetProblemNoSteam,
        ResetProblem.NoGame => Strings.ResetProblemNoGame,
        ResetProblem.BrokenAutoexec => Format(Strings.ResetProblemAutoexecFormat, step.Detail),
        ResetProblem.LaunchChangedInSteam => Strings.ResetProblemLaunchChanged,
        ResetProblem.TransferChangedSince => Strings.ResetProblemTransferChanged,
        ResetProblem.BrokenGrids => Format(Strings.ResetProblemGridsFormat, step.Detail),
        _ => Format(Strings.ResetProblemFailedFormat, step.Detail),
    };

    private static string Format(string format, string? value) =>
        string.Format(CultureInfo.CurrentCulture, format, value ?? string.Empty);
}
