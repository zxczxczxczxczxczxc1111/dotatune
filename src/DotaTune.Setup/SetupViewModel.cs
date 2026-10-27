using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Diagnostics;
using DotaTune.Setup.Resources;

namespace DotaTune.Setup;

/// <summary>What a button of the installer window does.</summary>
internal enum SetupAction
{
    Install,
    Open,
    OpenLogs,
    Close,
}

/// <summary>
/// The installer window: what is installed now and what the main button does about it, then
/// the way through the installation to the opened program or to the log.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly ISetupSteps _steps;
    private readonly FileLog _log;
    private readonly SetupCase _case;
    private readonly string _version;
    private readonly string? _installed;
    private SetupAction _primary;
    private SetupAction? _secondary;

    internal SetupViewModel(SetupCase setupCase, Version version, Version? installed, ISetupSteps steps, FileLog log)
    {
        ArgumentNullException.ThrowIfNull(version);
        _case = setupCase;
        _version = version.ToString(3);
        _installed = installed?.ToString(3);
        _steps = steps;
        _log = log;
        ShowChoice();
    }

    /// <summary>The program is open or the person closed the installer from a button: the window goes.</summary>
    public event Action? Finished;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _lead = string.Empty;

    [ObservableProperty]
    private string? _note;

    [ObservableProperty]
    private string _primaryText = string.Empty;

    [ObservableProperty]
    private string? _secondaryText;

    /// <summary>The installation runs: no buttons, and the window cannot be closed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand), nameof(SecondaryCommand))]
    [NotifyPropertyChangedFor(nameof(ShowButtons))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isFailed;

    public bool ShowButtons => !IsBusy;

    private bool CanAct => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task PrimaryAsync() => RunAsync(_primary);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task SecondaryAsync() => _secondary is { } action ? RunAsync(action) : Task.CompletedTask;

    private void ShowChoice()
    {
        string installed = _installed ?? string.Empty;
        (Title, Lead, _primary, _secondary) = _case switch
        {
            SetupCase.Fresh => (SetupStrings.SetupFreshTitle, Format(SetupStrings.SetupFreshLeadFormat, _version), SetupAction.Install, (SetupAction?)null),
            SetupCase.Update => (SetupStrings.SetupUpdateTitle, Format(SetupStrings.SetupUpdateLeadFormat, installed, _version), SetupAction.Install, null),
            SetupCase.Same => (Format(SetupStrings.SetupSameTitleFormat, _version), SetupStrings.SetupSameLead, SetupAction.Open, SetupAction.Install),
            SetupCase.Newer => (SetupStrings.SetupNewerTitle, Format(SetupStrings.SetupNewerLeadFormat, installed, _version), SetupAction.Open, SetupAction.Install),
            _ => (SetupStrings.SetupUpdateTitle, Format(SetupStrings.SetupUnknownLeadFormat, _version), SetupAction.Install, SetupAction.Open),
        };
        // Only where the main button installs: for the same or a newer version it just opens the program.
        Note = _case is SetupCase.Update or SetupCase.Unknown && _steps.AppRunning() ? SetupStrings.SetupAppRunning : null;
        if (!_steps.HasPayload)
        {
            Fail(SetupStrings.SetupNoPayload);
            (_primary, _secondary) = (SetupAction.Close, null);
        }

        ShowButtonTexts();
        _log.Info($"Installer {_version} found {installed}, case {_case}");
    }

    private async Task RunAsync(SetupAction action)
    {
        switch (action)
        {
            case SetupAction.Install:
                await InstallAsync();
                break;
            case SetupAction.Open:
                if (_steps.OpenApp())
                {
                    Finished?.Invoke();
                }
                else
                {
                    Show(SetupStrings.SetupOpenFailedTitle, SetupStrings.SetupFailedLead, SetupAction.Install, SetupAction.OpenLogs);
                }

                break;
            case SetupAction.OpenLogs:
                _steps.OpenLogs();
                break;
            default:
                Finished?.Invoke();
                break;
        }
    }

    private async Task InstallAsync()
    {
        IsBusy = true;
        IsFailed = false;
        Note = null;
        Title = Format(_case == SetupCase.Update ? SetupStrings.SetupUpdatingFormat : SetupStrings.SetupInstallingFormat, _version);
        int code;
        try
        {
            if (_steps.AppRunning())
            {
                Lead = SetupStrings.SetupClosingApp;
                await _steps.CloseAppAsync();
            }

            Lead = SetupStrings.SetupWorking;
            code = await _steps.InstallAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            _log.Error("Installation did not run", ex);
            code = -1;
        }
        finally
        {
            IsBusy = false;
        }

        if (code != 0)
        {
            // Velopack moves the old folder back when the new one fails, so a failed update leaves the old
            // version. A setup still running has not failed yet, and nothing can be said about the old one.
            bool kept = _case != SetupCase.Fresh && code != SetupSteps.StillRunning;
            Fail(kept ? SetupStrings.SetupFailedKeptLead : SetupStrings.SetupFailedLead);
            return;
        }

        if (_steps.OpenApp())
        {
            Finished?.Invoke();
            return;
        }

        Show(SetupStrings.SetupNotOpenedTitle, SetupStrings.SetupNotOpenedLead, SetupAction.Close, SetupAction.OpenLogs);
    }

    private void Fail(string lead)
    {
        Show(SetupStrings.SetupFailedTitle, lead, SetupAction.OpenLogs, SetupAction.Close);
        IsFailed = true;
    }

    private void Show(string title, string lead, SetupAction primary, SetupAction? secondary)
    {
        (Title, Lead, _primary, _secondary) = (title, lead, primary, secondary);
        ShowButtonTexts();
    }

    private void ShowButtonTexts()
    {
        PrimaryText = ButtonText(_primary);
        SecondaryText = _secondary is { } secondary ? ButtonText(secondary) : null;
    }

    private string ButtonText(SetupAction action) => action switch
    {
        SetupAction.Install => _case switch
        {
            SetupCase.Fresh => SetupStrings.SetupInstall,
            SetupCase.Update => SetupStrings.SetupUpdate,
            SetupCase.Newer => Format(SetupStrings.SetupDowngradeFormat, _version),
            _ => SetupStrings.SetupReinstall,
        },
        SetupAction.Open => SetupStrings.SetupOpen,
        SetupAction.OpenLogs => SetupStrings.SetupOpenLogs,
        _ => SetupStrings.SetupClose,
    };

    private static string Format(string format, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, format, values);
}
