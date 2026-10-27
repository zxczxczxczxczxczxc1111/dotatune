using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Game;
using DotaTune.Core.Pack;
using DotaTune.Resources;

namespace DotaTune.ViewModels;

public enum Tone
{
    Ok,
    Attention,
    Error,

    /// <summary>Nothing read yet: the plate shows a grey mark.</summary>
    Neutral,
}

public sealed record FactRow(string Label, string Value);

public sealed record FactGroup(string Title, IReadOnlyList<FactRow> Rows);

/// <summary>The "Game status" page: what DotaTune read about Steam and the game, what it would do, and the pack itself.</summary>
public sealed partial class GameStateViewModel : ObservableObject, IActivatedPage
{
    private readonly AppServices _services;

    // Readings and changes of the game go one at a time, so a click during a reading waits for it
    // and the page keeps its plate and buttons instead of blinking.
    private readonly SemaphoreSlim _turn = new(1, 1);
    private int _running;
    private bool _readingQueued;

    internal GameStateViewModel(AppServices services, string title, string description)
    {
        _services = services;
        Title = title;
        Description = description;
        Summary = Strings.ReadingGame;
    }

    public void Activated()
    {
        if (!IsLoading && !IsPlaying)
        {
            _ = RefreshCommand.ExecuteAsync(null);
        }
    }

    public string Title { get; }

    public string Description { get; }

    /// <summary>A reading or a change of the game is under way or waits for its turn.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>The pack is being installed or removed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallPackCommand), nameof(RemovePackCommand), nameof(PlayCommand))]
    private bool _isChanging;

    /// <summary>The game is being started and DotaTune waits for its search paths.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallPackCommand), nameof(RemovePackCommand), nameof(PlayCommand))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsAttention), nameof(IsError))]
    private Tone _tone = Tone.Neutral;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _action = string.Empty;

    [ObservableProperty]
    private string? _note;

    [ObservableProperty]
    private IReadOnlyList<FactGroup> _groups = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallPackCommand))]
    [NotifyPropertyChangedFor(nameof(PlayIsAccent))]
    private bool _installable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemovePackCommand))]
    private bool _removable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    private bool _playable;

    public bool IsOk => Tone == Tone.Ok;

    public bool IsAttention => Tone == Tone.Attention;

    public bool IsError => Tone == Tone.Error;

    /// <summary>One accent button at a time: while the pack can be installed, that is the main action.</summary>
    public bool PlayIsAccent => !Installable;

    private bool CanInstall => Installable && !IsChanging && !IsPlaying;

    private bool CanRemove => Removable && !IsChanging && !IsPlaying;

    private bool CanPlay => Playable && !IsChanging && !IsPlaying;

    /// <summary>The button stays lit while reading: a reading already waiting for its turn sees everything done before it.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RefreshAsync()
    {
        if (_readingQueued)
        {
            return Task.CompletedTask;
        }

        _readingQueued = true;
        return InTurn(() =>
        {
            _readingQueued = false;
            return ReadAndShowAsync();
        });
    }

    /// <summary>Until the new reading lands, the page keeps showing the previous one.</summary>
    private async Task ReadAndShowAsync()
    {
        (GameState state, PackHealth health) = await Task.Run(ReadAll);
        Show(state, health);
        _services.Log.Info($"Game state: problem {state.Problem}, decision {state.Decision}, pack {health}, build {state.Dota?.Build}");
    }

    private Task InTurn(Func<Task> work) => InTurn(async () =>
    {
        await work();
        return true;
    });

    private async Task<T> InTurn<T>(Func<Task<T>> work)
    {
        _running++;
        IsLoading = true;
        try
        {
            await _turn.WaitAsync();
            try
            {
                return await work();
            }
            finally
            {
                _turn.Release();
            }
        }
        finally
        {
            IsLoading = --_running > 0;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallPackAsync() => Change(() => Installable, () => RefusalText(_services.InstallPack()));

    /// <summary>What to tell about a refused install; null when there is nothing to add to the page.</summary>
    internal static string? RefusalText(InstallRefusal? refusal) => refusal switch
    {
        InstallRefusal.DotaRunning => Strings.DotaRunningNote,
        InstallRefusal.NoFreeSlot => Strings.RefusedNoFreeSlot,
        InstallRefusal.WrittenFileDiffers => Strings.RefusedWrittenFileDiffers,
        _ => null,
    };

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemovePackAsync() => Change(() => Removable, () =>
    {
        _services.Packs.Remove(_services.ReadGame());
        return null;
    });

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        IsPlaying = true;
        Note = Strings.PlayWaiting;
        try
        {
            Tone? tone;
            string message;
            try
            {
                // The MMR line of the new session counts from the newest reading, so the pack gets it first.
                // The pack is written here, so it waits for a reading under way.
                (GameState game, InstallState? pack) = await InTurn(() => Task.Run(() =>
                {
                    GameState read = _services.ReadGame();
                    _services.RefreshMmr(read);
                    return (read, _services.Packs.Current);
                }));
                PlayResult result = await Task.Run(() => _services.Launcher.PlayAsync(game, pack));
                (tone, message) = PlayText(result);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _services.Log.Error("Play failed", ex);
                (tone, message) = (Tone.Error, Format(Strings.PlayErrorFormat, ex.Message));
            }

            // The buttons stay off until the page shows the running game, or they would light up for a moment.
            await InTurn(async () =>
            {
                await ReadAndShowAsync();
                Note = message;
                if (tone is { } shown)
                {
                    Tone = shown;
                }
            });
        }
        finally
        {
            IsPlaying = false;
        }
    }

    private static (Tone?, string) PlayText(PlayResult result) => result.Outcome switch
    {
        PlayOutcome.PackLoaded => (Tone.Ok, Strings.PlayPackLoaded),
        PlayOutcome.PackNotLoaded => (Tone.Attention, Strings.PlayPackNotLoaded),
        PlayOutcome.Started => (null, Strings.PlayStarted),
        PlayOutcome.DidNotStart => (Tone.Attention, Strings.PlayDidNotStart),
        PlayOutcome.ClosedEarly => (Tone.Attention, Strings.PlayClosedEarly),
        PlayOutcome.NoSearchPaths => (Tone.Attention, Strings.PlayNoSearchPaths),
        PlayOutcome.AlreadyRunning => (null, Strings.PlayAlreadyRunning),
        PlayOutcome.NoGame => (Tone.Error, Strings.ProblemDotaNotFound),
        _ => throw new InvalidOperationException($"Unknown play outcome {result.Outcome}"),
    };

    /// <summary>
    /// Runs a change of the game off the UI thread in its turn, then reads everything again.
    /// A click made during a reading is checked again against what that reading found.
    /// </summary>
    private async Task Change(Func<bool> stillWanted, Func<string?> change)
    {
        IsChanging = true;
        try
        {
            await InTurn(async () =>
            {
                if (!stillWanted())
                {
                    return;
                }

                string? message;
                try
                {
                    message = await Task.Run(change);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                {
                    _services.Log.Error("Pack change failed", ex);
                    message = Format(Strings.PackErrorFormat, ex.Message);
                }

                await ReadAndShowAsync();
                if (message is not null)
                {
                    Note = message;
                }
            });
        }
        finally
        {
            IsChanging = false;
        }
    }

    private (GameState, PackHealth) ReadAll()
    {
        GameState state = _services.ReadGame();
        try
        {
            return (state, _services.Packs.Check(state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _services.Log.Error("Pack state is unreadable", ex);
            return (state, PackHealth.Changed);
        }
    }

    internal void Show(GameState state, PackHealth health = PackHealth.NotInstalled)
    {
        Note = state.DotaRunning ? Strings.DotaRunningNote : null;
        Groups = BuildGroups(state);
        Removable = health != PackHealth.NotInstalled && !state.DotaRunning;
        Playable = state.Steam is not null && state.Dota is not null && !state.DotaRunning;
        Installable = !state.DotaRunning
            && health != PackHealth.Ready
            && state.Decision?.Action is PackAction.UseVoiceFolder or PackAction.SwitchToCarrier or PackAction.UseLaunchLanguageFolder;

        if (state.Problem is GameProblem problem and not GameProblem.NoAccount || state.Decision is null)
        {
            Tone = Tone.Error;
            Summary = ProblemText(state.Problem ?? GameProblem.DotaNotFound, state.ProblemDetail);
            Action = string.Empty;
            return;
        }

        PackDecision decision = state.Decision;
        string? language = decision.TargetFolder ?? decision.LoadedFolder;
        (Tone, Action) = decision.Action switch
        {
            PackAction.UseVoiceFolder => (Tone.Ok, Strings.ActionUseVoiceFolder),
            PackAction.SwitchToCarrier => (Tone.Ok, Format(Strings.ActionSwitchToCarrier, language)),
            PackAction.UseLaunchLanguageFolder => (Tone.Ok, Format(Strings.ActionUseLaunchLanguageFolder, language)),
            PackAction.OfferRemoveEnglishOverride => (Tone.Attention, Strings.ActionOfferRemoveEnglishOverride),
            PackAction.OfferRemoveForeignOverride => (Tone.Attention, Format(Strings.ActionOfferRemoveForeignOverride, LaunchLanguage(state))),
            PackAction.StartGameOnce => (Tone.Attention, Strings.ActionStartGameOnce),
            PackAction.NoCarrier => (Tone.Error, Strings.ActionNoCarrier),
            PackAction.UnknownBootFormat => (Tone.Error, Strings.ActionUnknownBootFormat),
            _ => throw new InvalidOperationException($"Unknown pack action {decision.Action}"),
        };

        Summary = health switch
        {
            PackHealth.Ready => Strings.StatePackReady,
            PackHealth.Missing => Strings.HealthMissing,
            PackHealth.Changed => Strings.HealthChanged,
            PackHealth.FolderNotLoaded => Strings.HealthFolderNotLoaded,
            PackHealth.OtherBuild => Strings.HealthOtherBuild,
            _ => Strings.StateNoPack,
        };
        if (health == PackHealth.Ready)
        {
            // Once installed, the plan text describes the past.
            (Tone, Action) = (Tone.Ok, string.Empty);
        }
        else if (health != PackHealth.NotInstalled)
        {
            Tone = Tone.Attention;
        }
        else if (state.Folders.Any(f => f.Paks.Any(p => p.Owner == PakOwner.DotaTune)))
        {
            // A pack with the marker but no record: left when the program's data folder was lost.
            (Tone, Action) = (Tone.Attention, $"{Strings.StateOldPack} {Action}".Trim());
        }

        if (state.Problem == GameProblem.NoAccount)
        {
            Tone = Tone.Attention;
            Action = $"{Action} {Strings.ProblemNoAccount}".Trim();
        }
    }

    private static string LaunchLanguage(GameState state) =>
        Core.Steam.LaunchOptions.LanguageOverride(state.LaunchOptions) ?? string.Empty;

    private static string ProblemText(GameProblem problem, string? detail) => problem switch
    {
        GameProblem.SteamNotFound => Strings.ProblemSteamNotFound,
        GameProblem.DotaNotFound => Strings.ProblemDotaNotFound,
        GameProblem.NoAccount => Strings.ProblemNoAccount,
        _ => Format(Strings.ProblemUnreadableFormat, detail ?? string.Empty),
    };

    private static List<FactGroup> BuildGroups(GameState state)
    {
        var groups = new List<FactGroup>();
        if (state.Dota is { } dota)
        {
            List<FactRow> rows =
            [
                new FactRow(Strings.FactGameFolder, dota.GameRoot),
                new FactRow(Strings.FactBuild, dota.Build ?? Strings.ValueUnknown),
                new FactRow(Strings.FactDotaNow, state.DotaRunning ? Strings.ValueRunning : Strings.ValueClosed),
                new FactRow(Strings.FactVoiceLanguage, state.Boot?.AudioLanguage ?? Strings.ValueUnknown),
            ];
            int otherMods = state.Folders.Sum(f => f.Paks.Count(p => p.Owner is not (PakOwner.Valve or PakOwner.DotaTune)));
            if (otherMods > 0)
            {
                // They take the places the pack could go to, so the player sees why it found none.
                rows.Add(new FactRow(Strings.FactOtherMods, otherMods.ToString(CultureInfo.CurrentCulture)));
            }

            groups.Add(new FactGroup(Strings.GroupGame, rows));
        }

        if (state.Steam is { } steam)
        {
            string account = steam.Account is null
                ? Strings.ValueUnknown
                : Format(steam.Account.SignedInNow ? Strings.AccountNowFormat : Strings.AccountLastFormat, AccountTitle(steam.Account));
            string options = state.LaunchOptions switch
            {
                null => Strings.ValueUnknown,
                "" => Strings.ValueNone,
                var text => text,
            };
            groups.Add(new FactGroup(Strings.GroupSteam,
            [
                new FactRow(Strings.FactSteamFolder, steam.Root),
                new FactRow(Strings.FactAccount, account),
                new FactRow(Strings.FactLaunchOptions, options),
            ]));
        }

        return groups;
    }

    private static string AccountTitle(Core.Steam.SteamAccount account) =>
        account.AccountName.Length > 0 ? account.AccountName : account.AccountId.ToString(CultureInfo.InvariantCulture);

    private static string Format(string format, string? value) =>
        string.Format(CultureInfo.CurrentCulture, format, value);
}
