using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Game;
using DotaTune.Core.Menu;
using DotaTune.Core.Pack;
using DotaTune.Resources;

namespace DotaTune.ViewModels;

/// <summary>One choice on the page: the game's own background, black, a sample or an added picture.</summary>
public sealed class MenuTile
{
    internal MenuTile(string? id, string title, Bitmap? preview)
    {
        Id = id;
        Title = title;
        Preview = preview;
    }

    /// <summary>What the settings keep; null for the game's own background.</summary>
    public string? Id { get; }

    /// <summary>The name a screen reader says; shown on the card only for the two plain choices.</summary>
    public string Title { get; }

    /// <summary>A small copy of the picture; null for the game's own and black backgrounds.</summary>
    public Bitmap? Preview { get; }

    /// <summary>A picture speaks for itself, so only the game's own and black backgrounds carry a caption.</summary>
    public bool ShowsTitle => IsGame || IsBlack;

    public bool IsGame => Id is null;

    public bool IsBlack => Id == MenuPictures.BlackId;

    public bool IsAdded => MenuPictures.IsAdded(Id);
}

/// <summary>
/// The "Menu background" page: the game's own background, black, a sample or a picture of one's
/// own. A choice is saved at once and reaches the game when the pack is built again.
/// </summary>
public sealed partial class MenuBackgroundViewModel : ObservableObject, IActivatedPage
{
    private const int PreviewWidth = 320;
    private readonly AppServices _services;
    private GameState? _game;
    private InstallState? _pack;
    private PackHealth _health;
    private MenuSupport _support = MenuSupport.Unknown;
    private string? _supportBuild;
    private bool _picturesLoaded;
    private bool _restoring;

    private enum MenuSupport
    {
        Unknown,
        None,
        BlackOnly,
        All,
    }

    internal MenuBackgroundViewModel(AppServices services, string title, string description)
    {
        _services = services;
        Title = title;
        Description = description;
        Tiles = [new MenuTile(null, Strings.MenuGame, null), new MenuTile(MenuPictures.BlackId, Strings.MenuBlack, null)];
        _restoring = true;
        Selected = Tiles.FirstOrDefault(t => t.Id == services.Settings.Load().MenuBackground) ?? Tiles[0];
        _restoring = false;
    }

    public string Title { get; }

    public string Description { get; }

    public ObservableCollection<MenuTile> Tiles { get; }

    /// <summary>Asks for a picture file; set by the view. Null when cancelled.</summary>
    public Func<Task<string?>>? PickPicturePath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyCanExecuteChangedFor(nameof(RemovePictureCommand))]
    private MenuTile? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(AddPictureCommand), nameof(RemovePictureCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isOk;

    [ObservableProperty]
    private bool _isAttention;

    [ObservableProperty]
    private string? _note;

    /// <summary>"Install pack" or "Rebuild pack"; the button is hidden when the pack already matches.</summary>
    [ObservableProperty]
    private string _applyText = Strings.InstallPack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _canApplyNow;

    /// <summary>An added picture is chosen, so it can be removed.</summary>
    public bool CanRemove => Selected?.IsAdded == true;

    private bool CanApply => CanApplyNow && !IsBusy;

    private bool NotBusy => !IsBusy;

    private bool CanRemoveNow => CanRemove && !IsBusy;

    public void Activated()
    {
        if (!IsBusy)
        {
            _ = RefreshAsync();
        }
    }

    partial void OnSelectedChanged(MenuTile? value)
    {
        if (_restoring || value is null)
        {
            return;
        }

        Save(value.Id);
        Show();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        IsBusy = true;
        // The tiles stay clickable while the pack is built, so the pack is checked against what it was built for.
        string? chosen = Selected?.Id;
        string? message;
        try
        {
            message = GameStateViewModel.RefusalText(await Task.Run(_services.InstallPack));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _services.Log.Error("Pack rebuild for the menu background failed", ex);
            message = string.Format(CultureInfo.CurrentCulture, Strings.PackErrorFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
        if (message is null && _pack is not null && _pack.MenuBackground != chosen)
        {
            message = Strings.MenuNotApplied;
        }

        Note = message ?? Note;
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task AddPictureAsync()
    {
        if (PickPicturePath is null || await PickPicturePath() is not string path)
        {
            return;
        }

        IsBusy = true;
        try
        {
            (string id, Bitmap? preview) = await Task.Run(() =>
            {
                string id = _services.MenuPictures.Add(path);
                return (id, LoadPreview(id));
            });
            MenuTile? tile = Tiles.FirstOrDefault(t => t.Id == id);
            if (tile is null)
            {
                tile = new MenuTile(id, Strings.MenuOwnPicture, preview);
                Tiles.Add(tile);
            }
            else
            {
                // The same picture was added before: its tile is chosen again.
                preview?.Dispose();
            }

            Selected = tile;
        }
        catch (InvalidDataException ex)
        {
            _services.Log.Info($"Menu picture {path} was not added: {ex.Message}");
            Note = Strings.MenuPictureUnreadable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Error($"Menu picture {path} was not added", ex);
            Note = string.Format(CultureInfo.CurrentCulture, Strings.MenuPictureErrorFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveNow))]
    private void RemovePicture()
    {
        MenuTile tile = Selected!;
        try
        {
            _services.MenuPictures.Remove(tile.Id!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Error($"Menu picture {tile.Id} was not removed", ex);
            Note = string.Format(CultureInfo.CurrentCulture, Strings.MenuRemoveErrorFormat, ex.Message);
            return;
        }

        Selected = Tiles[0];
        Tiles.Remove(tile);
        tile.Preview?.Dispose();
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            bool loadPictures = !_picturesLoaded;
            (_game, _pack, _health, _support, _supportBuild, IReadOnlyList<MenuTile> pictures) = await Task.Run(() => Read(loadPictures));
            if (loadPictures)
            {
                _picturesLoaded = true;
                AddPictures(pictures);
            }

            Note = BaseNote;
            Show();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private (GameState, InstallState?, PackHealth, MenuSupport, string?, IReadOnlyList<MenuTile>) Read(bool loadPictures)
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
            _services.Log.Error("Install state is unreadable on the menu background page", ex);
            (pack, health) = (null, PackHealth.NotInstalled);
        }

        (MenuSupport support, string? build) = (_support, _supportBuild);
        if (game.Dota is { } dota && (support == MenuSupport.Unknown || build != dota.Build))
        {
            (support, build) = (Support(dota), dota.Build);
        }

        List<MenuTile> pictures = [];
        if (loadPictures)
        {
            foreach (string name in MenuPictures.Samples)
            {
                string id = MenuPictures.SampleId(name);
                pictures.Add(new MenuTile(id, Strings.MenuSample, LoadPreview(id)));
            }

            foreach (string id in _services.MenuPictures.Added())
            {
                pictures.Add(new MenuTile(id, Strings.MenuOwnPicture, LoadPreview(id)));
            }
        }

        return (game, pack, health, support, build, pictures);
    }

    /// <summary>What this build of the game lets the pack do with the menu; read once per build.</summary>
    private MenuSupport Support(DotaInstall dota)
    {
        try
        {
            GameMenuBackground menu = GameMenuBackground.Read(dota.DotaFolder);
            return menu.CanShowPicture ? MenuSupport.All : menu.CanShowBlack ? MenuSupport.BlackOnly : MenuSupport.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _services.Log.Warning($"Menu background files of build {dota.Build} are not readable: {ex.Message}");
            return MenuSupport.None;
        }
    }

    /// <summary>A preview of the picture, or null with a log line when it cannot be read.</summary>
    private Bitmap? LoadPreview(string id)
    {
        try
        {
            using Stream stream = _services.MenuPictures.Open(id);
            return Bitmap.DecodeToWidth(stream, PreviewWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _services.Log.Warning($"Preview of menu picture {id} is not readable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Puts the samples and added pictures after the two plain choices and selects the saved one.</summary>
    private void AddPictures(IReadOnlyList<MenuTile> pictures)
    {
        string? saved = _services.Settings.Load().MenuBackground;
        _restoring = true;
        try
        {
            foreach (MenuTile tile in pictures)
            {
                Tiles.Add(tile);
            }

            Selected = Tiles.FirstOrDefault(t => t.Id == saved) ?? Tiles[0];
        }
        finally
        {
            _restoring = false;
        }

        if (saved is not null && Selected?.Id != saved)
        {
            // The picture was deleted outside DotaTune: the game's own background is what is left.
            _services.Log.Warning($"Saved menu background {saved} is gone, the game's own is chosen");
            Save(null);
        }
    }

    private void Save(string? id)
    {
        try
        {
            _services.Settings.Save(_services.Settings.Load() with { MenuBackground = id });
            Note = BaseNote;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Error("Could not save the menu background", ex);
            Note = string.Format(CultureInfo.CurrentCulture, Strings.EffectsSaveErrorFormat, ex.Message);
        }
    }

    /// <summary>Shows how the installed pack relates to the choice; the note is left to the caller.</summary>
    private void Show()
    {
        if (_game is null)
        {
            return;
        }

        string? chosen = Selected?.Id;
        bool installable = PackService.RefusalFor(_game) is null && Problem(chosen) is null;
        if (_pack is null)
        {
            Summary = Strings.MenuNoPack;
            (IsOk, IsAttention) = (false, false);
            ApplyText = Strings.InstallPack;
            CanApplyNow = installable && chosen is not null;
            return;
        }

        // A pack that is gone, replaced or from an older build has to be built again whatever the choice.
        bool matches = _health == PackHealth.Ready && _pack.MenuBackground == chosen;
        Summary = _health switch
        {
            PackHealth.Missing => Strings.HealthMissing,
            PackHealth.Changed => Strings.HealthChanged,
            PackHealth.FolderNotLoaded => Strings.HealthFolderNotLoaded,
            PackHealth.OtherBuild => Strings.HealthOtherBuild,
            _ => matches ? Strings.MenuPackMatches : Strings.MenuPackDiffers,
        };
        (IsOk, IsAttention) = (matches, !matches);
        ApplyText = Strings.RebuildPack;
        CanApplyNow = !matches && installable;
    }

    /// <summary>Why the game cannot take this choice; null when it can or when the game is not known yet.</summary>
    private string? Problem(string? id) => (_support, MenuPictures.KindOf(id)) switch
    {
        (_, MenuBackgroundKind.Game) => null,
        (MenuSupport.None, _) => Strings.MenuNotSupported,
        (MenuSupport.BlackOnly, MenuBackgroundKind.Picture) => Strings.MenuPictureNotSupported,
        _ => null,
    };

    private string? BaseNote => Problem(Selected?.Id) ?? (_game?.DotaRunning == true ? Strings.DotaRunningNote : null);
}
