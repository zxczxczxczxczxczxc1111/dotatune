using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Config;
using DotaTune.Core.Effects;
using DotaTune.Core.Game;
using DotaTune.Core.Presets;
using DotaTune.Resources;

namespace DotaTune.ViewModels;

/// <summary>
/// Presets on the "Config and launch" page: the choice goes out as a DT1- code or a .dotatune
/// file, files only in the file. Loading shows what changes before anything is applied.
/// </summary>
public sealed partial class PresetBlockViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Func<Func<string?>, string, Task<string?>> _run;
    private readonly Func<PresetChoice> _currentChoice;
    private readonly Action<PresetReview> _applyChoice;
    private PresetReview? _review;

    /// <param name="run">Runs a change off the UI thread, reads the page again and returns the change's message.</param>
    /// <param name="currentChoice">The choice of the program now, for export.</param>
    /// <param name="applyChoice">Puts a loaded choice on the pages.</param>
    internal PresetBlockViewModel(AppServices services, Func<Func<string?>, string, Task<string?>> run, Func<PresetChoice> currentChoice, Action<PresetReview> applyChoice)
    {
        _services = services;
        _run = run;
        _currentChoice = currentChoice;
        _applyChoice = applyChoice;
    }

    /// <summary>Asks where to save a file; set by the view. Null when cancelled.</summary>
    public Func<Task<string?>>? PickSavePath { get; set; }

    /// <summary>Asks which file to open; set by the view. Null when cancelled.</summary>
    public Func<Task<string?>>? PickOpenPath { get; set; }

    /// <summary>Puts text on the clipboard; set by the view.</summary>
    public Func<string, Task>? CopyText { get; set; }

    [ObservableProperty]
    private bool _withHeroGrids;

    [ObservableProperty]
    private bool _withHotkeys;

    [ObservableProperty]
    private bool _withGameSettings;

    /// <summary>The code of the current choice, shown after it is copied.</summary>
    [ObservableProperty]
    private string? _code;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCodeCommand))]
    private string _importText = string.Empty;

    /// <summary>A loaded preset waiting for "apply".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending), nameof(HasHeroGrids), nameof(HasHotkeys), nameof(HasGameSettings))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private PresetPackage? _pending;

    [ObservableProperty]
    private IReadOnlyList<string> _changes = [];

    /// <summary>Names this version does not know; they are not applied.</summary>
    [ObservableProperty]
    private string? _unknown;

    [ObservableProperty]
    private bool _applyChoicePart = true;

    [ObservableProperty]
    private bool _applyHeroGrids = true;

    [ObservableProperty]
    private bool _applyHotkeys = true;

    [ObservableProperty]
    private bool _applyGameSettings = true;

    [ObservableProperty]
    private string? _note;

    /// <summary>The page is changing or reading the game; set by the page.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveFileCommand), nameof(OpenFileCommand), nameof(ApplyCommand))]
    private bool _isBusy;

    public bool HasPending => Pending is not null;

    public bool HasHeroGrids => Pending?.Parts.ContainsKey(PresetPart.HeroGrids) == true;

    public bool HasHotkeys => Pending?.Parts.ContainsKey(PresetPart.Hotkeys) == true;

    public bool HasGameSettings => Pending?.Parts.ContainsKey(PresetPart.GameSettings) == true;

    private bool NotBusy => !IsBusy;

    private bool CanLoadCode => !string.IsNullOrWhiteSpace(ImportText);

    private bool CanApply => Pending is not null && !IsBusy;

    /// <summary>Saves the current choice and the ticked files of the signed-in account.</summary>
    internal async Task SaveToAsync(string path)
    {
        bool grids = WithHeroGrids;
        bool hotkeys = WithHotkeys;
        bool settings = WithGameSettings;
        PresetChoice choice = _currentChoice();
        Note = await _run(() =>
        {
            GameState game = _services.ReadGame();
            var parts = new Dictionary<PresetPart, byte[]>();
            if (game.Steam is { Account: { } account } steam)
            {
                string folder = TransferFiles.GameFolder(steam.Root, account.AccountId);
                Take(PresetPart.HeroGrids, grids, TransferFiles.HeroGridPath);
                Take(PresetPart.Hotkeys, hotkeys, TransferFiles.HotkeysPath);
                if (settings && TransferFiles.GameSettings(folder) is byte[] content)
                {
                    parts[PresetPart.GameSettings] = Normalize(PresetPart.GameSettings, content);
                }

                void Take(PresetPart part, bool wanted, string relativePath)
                {
                    string file = TransferFiles.Full(folder, relativePath);
                    if (wanted && File.Exists(file))
                    {
                        parts[part] = Normalize(part, File.ReadAllBytes(file));
                    }
                }
            }

            var manifest = new PresetManifest(PresetFile.FormatVersion, AppServices.AppVersion, game.Dota?.Build, DateTimeOffset.Now,
                ["choice", .. parts.Keys.Select(p => p.ToString())]);
            PresetFile.Write(path, manifest, choice, parts);
            return string.Format(CultureInfo.CurrentCulture, Strings.PresetSavedFormat, Path.GetFileName(path));
        }, Strings.PresetErrorFormat);
    }

    /// <summary>Reads a .dotatune file and shows what it changes.</summary>
    internal async Task OpenFromAsync(string path)
    {
        try
        {
            Show(await Task.Run(() => PresetFile.Read(path)));
        }
        catch (Exception ex) when (ex is PresetException or IOException or UnauthorizedAccessException)
        {
            _services.Log.Error($"Preset file {path} was not loaded", ex);
            Note = ProblemText(ex);
        }
    }

    /// <summary>Shows the code of the current choice and puts it on the clipboard.</summary>
    [RelayCommand]
    private async Task CopyCodeAsync()
    {
        Code = PresetCode.Encode(_currentChoice());
        if (CopyText is null)
        {
            Note = null;
            return;
        }

        try
        {
            await CopyText(Code);
            Note = string.Format(CultureInfo.CurrentCulture, Strings.PresetCodeCopiedFormat, Code.Length);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            // Another program holds the Windows clipboard; the code is on the page to copy by hand.
            _services.Log.Error("The preset code did not reach the clipboard", ex);
            Note = Strings.PresetCopyFailed;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task SaveFileAsync()
    {
        if (PickSavePath is not null && await PickSavePath() is string path)
        {
            await SaveToAsync(path);
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task OpenFileAsync()
    {
        if (PickOpenPath is not null && await PickOpenPath() is string path)
        {
            await OpenFromAsync(path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadCode))]
    private void LoadCode()
    {
        try
        {
            Show(new PresetPackage(null, PresetCode.Decode(ImportText), new Dictionary<PresetPart, byte[]>()));
        }
        catch (PresetException ex)
        {
            _services.Log.Info($"Preset code was not loaded: {ex.Message}");
            Note = ProblemText(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        PresetPackage package = Pending!;
        PresetReview review = _review!;
        bool choice = ApplyChoicePart;
        var parts = package.Parts
            .Where(p => p.Key switch
            {
                PresetPart.HeroGrids => ApplyHeroGrids,
                PresetPart.Hotkeys => ApplyHotkeys,
                _ => ApplyGameSettings,
            })
            .ToDictionary(p => p.Key, p => p.Value);
        bool done = false;
        TransferResult? applied = null;
        string? message = await _run(() =>
        {
            if (parts.Count == 0)
            {
                done = true;
                return null;
            }

            GameState game = _services.ReadGame();
            if (game.Steam is not { Account: { } account } steam)
            {
                return Strings.ProblemNoAccount;
            }

            IReadOnlyList<(string Path, byte[] Content)> files = PresetFile.TargetFiles(TransferFiles.GameFolder(steam.Root, account.AccountId), parts);
            TransferResult result = _services.Transfer.ApplyFiles(game, account.AccountId, files);
            done = result.Refusal is null;
            applied = result;
            return TransferBlockViewModel.RefusalText(result.Refusal);
        }, Strings.PresetErrorFormat);

        if (done)
        {
            if (choice)
            {
                _applyChoice(review);
            }

            Pending = null;
            Changes = [];
            Unknown = null;
            // No result: nothing of the preset was chosen to write.
            message = applied is null
                ? string.Format(CultureInfo.CurrentCulture, Strings.PresetAppliedFormat, 0)
                : TransferBlockViewModel.WithSkippedGrids(string.Format(CultureInfo.CurrentCulture, Strings.PresetAppliedFormat, applied.Written), applied);
        }

        Note = message;
    }

    [RelayCommand]
    private void Cancel()
    {
        Pending = null;
        Changes = [];
        Unknown = null;
        Note = null;
    }

    private static byte[] Normalize(PresetPart part, byte[] content)
    {
        try
        {
            return PresetFile.Normalize(part, content);
        }
        catch (PresetException ex)
        {
            // The account's own file is broken: the error page shows it, the preset is not saved.
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    private void Show(PresetPackage package)
    {
        PresetReview review = PresetReview.Of(package.Choice);
        _review = review;
        var changes = new List<string>
        {
            review.Effects is null ? Strings.PresetEffectsAll
                : review.Effects.Count == 0 ? Strings.PresetEffectsNone
                : string.Format(CultureInfo.CurrentCulture, Strings.PresetEffectsFormat, string.Join(", ", review.Effects.Select(EffectTitle))),
        };
        List<string> config = ConfigWords((review.Config ?? new GameConfig()).Sanitized());
        changes.Add(config.Count == 0 ? Strings.PresetConfigNone
            : string.Format(CultureInfo.CurrentCulture, Strings.PresetConfigFormat, string.Join(", ", config)));
        changes.Add(review.Launch.Count == 0 ? Strings.PresetLaunchNone
            : string.Format(CultureInfo.CurrentCulture, Strings.PresetLaunchFormat, string.Join(' ', review.Launch)));
        Changes = changes;
        List<string> unknown = [.. review.UnknownEffects, .. review.UnknownLaunch];
        Unknown = unknown.Count == 0 ? null : string.Format(CultureInfo.CurrentCulture, Strings.PresetUnknownFormat, string.Join(", ", unknown));
        ApplyChoicePart = ApplyHeroGrids = ApplyHotkeys = ApplyGameSettings = true;
        Pending = package;
        Note = null;
    }

    /// <summary>The config choice in the words of the config page rather than as console lines.</summary>
    private static List<string> ConfigWords(GameConfig config)
    {
        List<string> words = [];
        AddColor(Strings.ConfigFriendly, config.FriendlyColor);
        AddColor(Strings.ConfigEnemy, config.EnemyColor);
        AddColor(Strings.ConfigNeutral, config.NeutralColor);
        if (config.CreepScale is double scale)
        {
            words.Add($"{Strings.ConfigCreepScale} {string.Format(CultureInfo.CurrentCulture, Strings.ConfigTimesFormat, scale)}");
        }

        if (config.HideFrontpageVideo)
        {
            words.Add(Strings.ConfigHideVideo);
        }

        if (config.MenuFpsLimit is int fps)
        {
            words.Add($"{Strings.ConfigMenuFps} {fps.ToString(CultureInfo.CurrentCulture)}");
        }

        if (config.NoParticleLights)
        {
            words.Add(Strings.ConfigNoParticleLights);
        }

        if (config.Minimalism)
        {
            words.Add(Strings.ConfigMinimalism);
        }

        return words;

        void AddColor(string title, Rgb? color)
        {
            if (color is Rgb value)
            {
                words.Add($"{title} {value.Hex}");
            }
        }
    }

    private static string EffectTitle(EffectGroup group) =>
        Strings.ResourceManager.GetString($"Effect{group}", CultureInfo.CurrentUICulture) ?? group.ToString();

    private static string ProblemText(Exception ex) => ex switch
    {
        PresetException { Problem: PresetProblem.Newer } => Strings.PresetNewer,
        PresetException { Problem: PresetProblem.Damaged } => Strings.PresetDamaged,
        PresetException { Problem: PresetProblem.TooLarge } => Strings.PresetTooLarge,
        PresetException => Strings.PresetNotAPreset,
        _ => string.Format(CultureInfo.CurrentCulture, Strings.PresetErrorFormat, ex.Message),
    };
}
