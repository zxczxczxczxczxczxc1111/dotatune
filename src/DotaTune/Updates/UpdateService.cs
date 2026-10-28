using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Diagnostics;
using DotaTune.Resources;
using Velopack.Exceptions;

namespace DotaTune.Updates;

public enum UpdateStage
{
    /// <summary>Started from the output folder or in tests: nothing to update.</summary>
    NotInstalled,

    /// <summary>Also before the check at start begins.</summary>
    Checking,

    Latest,

    NoConnection,

    /// <summary>GitHub refused the check: too many requests from this address.</summary>
    Limited,

    /// <summary>The check failed for another reason; the log says which.</summary>
    CheckFailed,

    Downloading,

    /// <summary>Downloaded; installs when DotaTune closes or on "Install now".</summary>
    Downloaded,

    /// <summary>Downloaded and asked to install: waits for DotaTune to finish writing the game files.</summary>
    Waiting,

    /// <summary>Velopack's updater is started and DotaTune closes.</summary>
    Installing,

    /// <summary>The download or the install failed; this version stays.</summary>
    Failed,

    /// <summary>Another DotaTune window downloads the version and installs it when it closes.</summary>
    Elsewhere,
}

/// <summary>
/// New versions of DotaTune. At start a check; a version found in the first seconds downloads
/// under a panel over the window and installs with a restart as soon as no page writes the game
/// files, one found later or put off with "Later" installs when DotaTune closes. Failures of the
/// check only show in the settings. Lives as long as the program, so a language switch, which
/// builds a new window, keeps the state; all members are for the UI thread.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    /// <summary>A version found later than this after start waits for the exit: the player is busy by then.</summary>
    internal static readonly TimeSpan PanelWindow = TimeSpan.FromSeconds(20);

    internal static readonly TimeSpan WorkPoll = TimeSpan.FromMilliseconds(500);

    private readonly Func<IAppUpdater>? _create;
    private readonly FileLog _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, Task> _delay;
    private IAppUpdater? _updater;
    private AppUpdate? _update;
    private DateTimeOffset _started;
    private bool _connectionLost;

    /// <param name="create">Makes the updater on the first check; null never goes online.</param>
    /// <param name="clock">Tests pass their own time for the panel window.</param>
    /// <param name="delay">Tests pass a wait that returns at once.</param>
    internal UpdateService(Func<IAppUpdater>? create, FileLog log, Func<DateTimeOffset>? clock = null, Func<TimeSpan, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _create = create;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _delay = delay ?? (wait => Task.Delay(wait));
        _stage = create is null ? UpdateStage.NotInstalled : UpdateStage.Checking;
        _started = _clock();
    }

    /// <summary>True while DotaTune writes the game files, so the install waits; set by the app.</summary>
    public Func<bool> IsWorking { get; set; } = () => false;

    /// <summary>
    /// True while another card is over the window, as what is new after an update: a version
    /// found then downloads for the exit instead of closing that card. Set by the app.
    /// </summary>
    public Func<bool> KeepsPanelClosed { get; set; } = () => false;

    /// <summary>Closes the program once Velopack's updater waits for it; set by the app.</summary>
    public Action Shutdown { get; set; } = () => { };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelTitle), nameof(PanelLead), nameof(PanelNote), nameof(IsFailed), nameof(ShowProgress),
        nameof(IsProgressIndeterminate), nameof(ShowLater), nameof(Status), nameof(CanCheck), nameof(CanInstallNow))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(InstallNowCommand))]
    private UpdateStage _stage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelLead), nameof(Status))]
    private int _percent;

    /// <summary>The panel over the window; while it is open the pages under it take no clicks.</summary>
    [ObservableProperty]
    private bool _isPanelOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelTitle), nameof(Status))]
    private string _newVersion = string.Empty;

    public static string CurrentVersion => AppServices.AppVersion;

    public string PanelTitle => Stage == UpdateStage.Failed
        ? Strings.UpdateFailedTitle
        : Format(Strings.UpdateTitleFormat, NewVersion);

    public string PanelLead => Stage switch
    {
        UpdateStage.Downloading => Format(Strings.UpdateDownloadingFormat, CurrentVersion, Percent),
        UpdateStage.Downloaded or UpdateStage.Waiting => Strings.UpdateWaiting,
        UpdateStage.Installing => Strings.UpdateInstalling,
        UpdateStage.Failed => Format(_connectionLost ? Strings.UpdateConnectionLostFormat : Strings.UpdateFailedFormat, CurrentVersion),
        _ => string.Empty,
    };

    public string? PanelNote => Stage == UpdateStage.Failed ? null : Strings.UpdateKeeps;

    public bool IsFailed => Stage == UpdateStage.Failed;

    public bool ShowProgress => Stage is UpdateStage.Downloading or UpdateStage.Waiting or UpdateStage.Installing;

    /// <summary>The bar shows percent while downloading and runs while waiting and installing.</summary>
    public bool IsProgressIndeterminate => Stage != UpdateStage.Downloading;

    /// <summary>While downloading and while waiting: the wait after "Play" lasts up to five minutes.</summary>
    public bool ShowLater => Stage is UpdateStage.Downloading or UpdateStage.Waiting;

    /// <summary>The line in the settings.</summary>
    public string Status => Stage switch
    {
        UpdateStage.NotInstalled => Strings.UpdateNotInstalled,
        UpdateStage.Checking => Strings.UpdateChecking,
        UpdateStage.Latest => Strings.UpdateLatest,
        UpdateStage.NoConnection => Strings.UpdateNoConnection,
        UpdateStage.Limited => Strings.UpdateLimited,
        UpdateStage.CheckFailed => Strings.UpdateCheckFailed,
        UpdateStage.Downloading => Format(Strings.UpdateDownloadingStatusFormat, NewVersion, Percent),
        UpdateStage.Downloaded => Format(Strings.UpdateDownloadedFormat, NewVersion),
        UpdateStage.Waiting or UpdateStage.Installing => Format(Strings.UpdateInstallingStatusFormat, NewVersion),
        UpdateStage.Elsewhere => Format(Strings.UpdateElsewhereFormat, NewVersion),
        _ => Format(Strings.UpdateFailedStatusFormat, NewVersion),
    };

    /// <summary>Not after <see cref="UpdateStage.Elsewhere"/>: a check would only meet the other window's lock again.</summary>
    public bool CanCheck => Stage is UpdateStage.Latest or UpdateStage.NoConnection or UpdateStage.Limited or UpdateStage.CheckFailed or UpdateStage.Failed;

    public bool CanInstallNow => Stage == UpdateStage.Downloaded;

    /// <summary>The check at start, right after the main window is shown.</summary>
    public Task StartAsync()
    {
        _started = _clock();
        return RunAsync(manual: false);
    }

    /// <summary>
    /// From Program once the window is closed: a downloaded version that waits goes in now,
    /// without starting DotaTune again.
    /// </summary>
    public void InstallOnExit()
    {
        if (Stage is not (UpdateStage.Downloaded or UpdateStage.Waiting) || _updater is null || _update is null)
        {
            return;
        }

        try
        {
            _updater.InstallAfterExit(_update, restart: false);
            _log.Info($"Version {_update.Version} installs after the exit");
        }
        catch (Exception ex)
        {
            // Velopack and Windows throw many kinds here; any of them only means this version stays.
            _log.Error($"Version {_update.Version} could not be installed on exit", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => RunAsync(manual: true);

    [RelayCommand(CanExecute = nameof(CanInstallNow))]
    private Task InstallNowAsync()
    {
        IsPanelOpen = true;
        return InstallWhenFreeAsync();
    }

    /// <summary>The download goes on under the settings, a wait for the work stops; the version installs when DotaTune closes.</summary>
    [RelayCommand]
    private void Later()
    {
        IsPanelOpen = false;
        _log.Info($"Update to {NewVersion} put off until the exit");
    }

    [RelayCommand]
    private void ClosePanel() => IsPanelOpen = false;

    private async Task RunAsync(bool manual)
    {
        if (_create is null)
        {
            return;
        }

        Stage = UpdateStage.Checking;
        AppUpdate? found;
        try
        {
            _updater ??= _create();
            if (!_updater.IsInstalled)
            {
                _log.Info("Started without installation, updates are off");
                Stage = UpdateStage.NotInstalled;
                return;
            }

            found = await Task.Run(_updater.CheckAsync);
        }
        catch (Exception ex)
        {
            // Velopack, HTTP and the disk throw many kinds; any of them only means no check this time.
            Stage = CheckFailure(ex);
            if (Stage == UpdateStage.CheckFailed)
            {
                _log.Error("Update check failed", ex);
            }
            else
            {
                _log.Warning($"Update check failed ({Stage}): {ex.Message}");
            }

            return;
        }

        if (found is null)
        {
            _log.Info($"Version {CurrentVersion} is the latest");
            Stage = UpdateStage.Latest;
            return;
        }

        _update = found;
        NewVersion = found.Version;
        bool panel = manual || (_clock() - _started < PanelWindow && !KeepsPanelClosed());
        _log.Info($"Version {found.Version} found, it downloads {(panel ? "under the panel" : "for the exit")}");
        Percent = 0;
        IsPanelOpen = panel;
        Stage = UpdateStage.Downloading;
        IAppUpdater updater = _updater;
        try
        {
            // Off the UI thread: Velopack builds the full package from a delta before it returns.
            await Task.Run(() => updater.DownloadAsync(found, percent => Dispatcher.UIThread.Post(() =>
            {
                if (Stage == UpdateStage.Downloading)
                {
                    Percent = Math.Clamp(percent, 0, 100);
                }
            })));
        }
        catch (AcquireLockFailedException ex)
        {
            // Another DotaTune downloads the same version and installs it; this one keeps quiet.
            _log.Warning($"Download of version {found.Version} left to another DotaTune: {ex.Message}");
            IsPanelOpen = false;
            Stage = UpdateStage.Elsewhere;
            return;
        }
        catch (Exception ex)
        {
            // As at the check: whatever the download throws, this version stays.
            _connectionLost = CheckFailure(ex) == UpdateStage.NoConnection;
            _log.Error($"Download of version {found.Version} failed", ex);
            Stage = UpdateStage.Failed;
            return;
        }

        _log.Info($"Version {found.Version} downloaded");
        Stage = UpdateStage.Downloaded;
        if (IsPanelOpen)
        {
            await InstallWhenFreeAsync();
        }
    }

    /// <summary>Waits until no page writes the game files, then hands over to Velopack and closes.</summary>
    private async Task InstallWhenFreeAsync()
    {
        if (_updater is null || _update is null)
        {
            return;
        }

        // The panel takes the clicks now, so no new work starts while this waits.
        Stage = UpdateStage.Waiting;
        while (IsWorking())
        {
            await _delay(WorkPoll);
            if (!IsPanelOpen)
            {
                // "Later": the version stays downloaded for the exit or "Install now".
                Stage = UpdateStage.Downloaded;
                return;
            }
        }

        Stage = UpdateStage.Installing;
        try
        {
            _updater.InstallAfterExit(_update, restart: true);
        }
        catch (Exception ex)
        {
            // As at the check: whatever starting the updater throws, this version stays.
            _connectionLost = false;
            _log.Error($"Version {_update.Version} could not be installed", ex);
            Stage = UpdateStage.Failed;
            return;
        }

        _log.Info($"Installing version {_update.Version}, DotaTune closes and starts again");
        Shutdown();
    }

    /// <summary>What a failed request means for the player; the exception may sit inside another.</summary>
    internal static UpdateStage CheckFailure(Exception exception)
    {
        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }:
                    return UpdateStage.Limited;
                case HttpRequestException { StatusCode: null } or SocketException or TimeoutException or TaskCanceledException:
                    return UpdateStage.NoConnection;
            }
        }

        return UpdateStage.CheckFailed;
    }

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
