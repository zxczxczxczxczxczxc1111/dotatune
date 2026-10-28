using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Accept;
using DotaTune.Core.Game;
using DotaTune.Core.Pack;
using DotaTune.Core.Settings;
using DotaTune.Resources;

namespace DotaTune.ViewModels;

/// <summary>
/// The "Auto accept" page: the switch, the delay and how the installed pack relates to them.
/// Both are saved at once and reach the game with the pack; another delay alone replaces one
/// file of a pack in place instead of building it again.
/// </summary>
public sealed partial class AutoAcceptViewModel : ObservableObject, IActivatedPage, IWorkingPage
{
    private readonly AppServices _services;
    private GameState? _game;
    private InstallState? _pack;
    private PackHealth _health;
    private WindowSupport _support = WindowSupport.Unknown;
    private string? _supportBuild;
    private bool _onlyDelay;
    private bool _restoring;

    private enum WindowSupport
    {
        Unknown,
        None,
        Script,
    }

    internal AutoAcceptViewModel(AppServices services, string title, string description)
    {
        _services = services;
        Title = title;
        Description = description;
        _restoring = true;
        var settings = services.Settings.Load();
        IsOn = settings.AutoAccept;
        Seconds = AutoAccept.Delay(settings.AutoAcceptDelay);
        _restoring = false;
    }

    public string Title { get; }

    public string Description { get; }

    public double MinSeconds => AutoAccept.MinDelay;

    public double MaxSeconds => AutoAccept.MaxDelay;

    [ObservableProperty]
    private bool _isOn;

    /// <summary>The slider value; the slider snaps to whole seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondsText))]
    private double _seconds;

    public string SecondsText => string.Format(CultureInfo.CurrentCulture, Strings.AutoAcceptSecondsFormat, Delay);

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

    [ObservableProperty]
    private string _applyText = Strings.InstallPack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _canApplyNow;

    private int Delay => AutoAccept.Delay((int)Math.Round(Seconds, MidpointRounding.AwayFromZero));

    /// <summary>The delay the pack should have; null when it should not accept.</summary>
    private int? Wanted => IsOn && _support != WindowSupport.None ? Delay : null;

    private bool CanApply => CanApplyNow && !IsBusy;

    public void Activated()
    {
        if (!IsBusy)
        {
            _ = RefreshAsync();
        }
    }

    partial void OnIsOnChanged(bool value) => Save(settings => settings with { AutoAccept = value });

    partial void OnSecondsChanged(double value) => Save(settings => settings with { AutoAcceptDelay = Delay });

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (_restoring)
        {
            return;
        }

        try
        {
            AppSettings settings = _services.Settings.Load();
            AppSettings changed = change(settings);
            if (changed != settings)
            {
                _services.Settings.Save(changed);
            }

            Note = BaseNote;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Error("Could not save the auto accept settings", ex);
            Note = string.Format(CultureInfo.CurrentCulture, Strings.EffectsSaveErrorFormat, ex.Message);
        }

        Show();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        IsBusy = true;
        int? wanted = Wanted;
        string? message;
        try
        {
            if (_onlyDelay && wanted is { } delay)
            {
                message = await Task.Run(() => _services.ReplaceAutoAccept(delay)) ? null : Strings.AutoAcceptNotReplaced;
            }
            else
            {
                message = GameStateViewModel.RefusalText(await Task.Run(_services.InstallPack));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _services.Log.Error("Pack change for auto accept failed", ex);
            message = string.Format(CultureInfo.CurrentCulture, Strings.PackErrorFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
        if (message is null && _pack is not null && wanted is not null && _pack.AutoAccept != wanted)
        {
            message = Strings.AutoAcceptNotApplied;
        }

        Note = message ?? Note;
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            (_game, _pack, _health, _support, _supportBuild) = await Task.Run(Read);
            Note = BaseNote;
            Show();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private (GameState, InstallState?, PackHealth, WindowSupport, string?) Read()
    {
        GameState game = _services.ReadGame();
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
            _services.Log.Error("Install state is unreadable on the auto accept page", ex);
            (pack, health) = (null, PackHealth.NotInstalled);
        }

        (WindowSupport support, string? build) = (_support, _supportBuild);
        if (game.Dota is { } dota && (support == WindowSupport.Unknown || build != dota.Build))
        {
            (support, build) = (Support(dota), dota.Build);
        }

        return (game, pack, health, support, build);
    }

    /// <summary>Whether this build of the game has the window script the lines go into; read once per build.</summary>
    private WindowSupport Support(DotaInstall dota)
    {
        try
        {
            if (AutoAccept.Problem(AutoAccept.ReadGameScript(dota.DotaFolder)) is { } problem)
            {
                _services.Log.Warning($"Auto accept does not fit build {dota.Build}: {problem}");
                return WindowSupport.None;
            }

            return WindowSupport.Script;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _services.Log.Warning($"Accept window script of build {dota.Build} is not readable: {ex.Message}");
            return WindowSupport.None;
        }
    }

    /// <summary>Shows how the installed pack relates to the switch and the delay; the note is left to the caller.</summary>
    private void Show()
    {
        if (_game is null)
        {
            return;
        }

        int? wanted = Wanted;
        bool installable = PackService.RefusalFor(_game) is null;
        _onlyDelay = false;
        if (_pack is null)
        {
            Summary = !IsOn ? Strings.StateNoPack : _support == WindowSupport.None ? Strings.AutoAcceptNotSupported : Strings.AutoAcceptNoPack;
            (IsOk, IsAttention) = (false, Summary == Strings.AutoAcceptNotSupported);
            ApplyText = Strings.InstallPack;
            CanApplyNow = installable && wanted is not null;
            return;
        }

        // A pack that is gone, replaced or from an older build has to be built again whatever the choice.
        bool ready = _health == PackHealth.Ready;
        bool matches = ready && _pack.AutoAccept == wanted;
        Summary = _health switch
        {
            PackHealth.Missing => Strings.HealthMissing,
            PackHealth.Changed => Strings.HealthChanged,
            PackHealth.FolderNotLoaded => Strings.HealthFolderNotLoaded,
            PackHealth.OtherBuild => Strings.HealthOtherBuild,
            _ when IsOn && _support == WindowSupport.None => Strings.AutoAcceptNotSupported,
            _ when matches => _pack.AutoAccept is { } on
                ? string.Format(CultureInfo.CurrentCulture, Strings.AutoAcceptPackOnFormat, on)
                : Strings.AutoAcceptPackOff,
            _ => _pack.AutoAccept is { } built
                ? string.Format(CultureInfo.CurrentCulture, Strings.AutoAcceptBuiltOnFormat, built)
                : Strings.AutoAcceptBuiltOff,
        };
        (IsOk, IsAttention) = (matches && _pack.AutoAccept is not null, !matches || Summary == Strings.AutoAcceptNotSupported);
        // Only the delay differs: one file of the pack is replaced and the rest stays.
        _onlyDelay = ready && _pack.AutoAccept is not null && wanted is not null;
        ApplyText = _onlyDelay ? Strings.AutoAcceptUpdatePack : Strings.RebuildPack;
        CanApplyNow = !matches && installable;
    }

    private string? BaseNote => _game?.DotaRunning == true ? Strings.DotaRunningNote : null;
}
