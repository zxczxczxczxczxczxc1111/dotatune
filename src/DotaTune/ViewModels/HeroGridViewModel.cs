using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Core.Game;
using DotaTune.Core.HeroGrids;
using DotaTune.Resources;

namespace DotaTune.ViewModels;

/// <summary>One layout on the page: what is in it, whether it should be in the game.</summary>
public sealed partial class GridRow : ObservableObject
{
    private readonly Action<GridRow> _changed;

    internal GridRow(string name, string description, HeroGridLayout? layout, bool isOn, Action<GridRow> changed)
    {
        Name = name;
        Description = description;
        Layout = layout;
        _isOn = isOn;
        _changed = changed;
    }

    public string Name { get; }

    public string Description { get; }

    /// <summary>What goes into the game; null keeps the layout that is there already.</summary>
    public HeroGridLayout? Layout { get; }

    [ObservableProperty]
    private bool _isOn;

    partial void OnIsOnChanged(bool value) => _changed(this);
}

/// <summary>
/// The "Hero grid" page: ready-made layouts for the hero grid editor of the account Dota
/// settings belong to. A tick is only a wish until the write button; the file of the game
/// says what is in it, so every opening of the page reads it again.
/// </summary>
public sealed partial class HeroGridViewModel : ObservableObject, IActivatedPage, IWorkingPage
{
    private readonly AppServices _services;

    /// <summary>Ticks set on the page and not written yet, by layout name; they outlive a new reading.</summary>
    private readonly Dictionary<string, bool> _wishes = new(StringComparer.Ordinal);

    /// <summary>Layouts taken from files during this run of the program, by name.</summary>
    private readonly Dictionary<string, (HeroGridLayout Layout, string File)> _fromFiles = new(StringComparer.Ordinal);

    private GameState? _game;
    private GridAccountState? _state;
    private GridCatalog _catalog;
    private string? _catalogProblem;

    /// <summary>Read when the page is first shown: most runs of the program never open it.</summary>
    private IReadOnlyList<HeroGridLayout>? _art;

    /// <summary>When a download failed last; the page does not try again on every opening.</summary>
    private DateTimeOffset? _downloadFailedAt;

    /// <summary>Our layouts in the game by name, from the last reading.</summary>
    private Dictionary<string, InstalledGrid> _installed = new(StringComparer.Ordinal);

    internal HeroGridViewModel(AppServices services, string title, string description)
    {
        _services = services;
        Title = title;
        Description = description;
        _catalog = services.GridCatalog.Current();
        CatalogText = CatalogLine();
    }

    /// <summary>After a failed download the page waits this long before it tries again by itself.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);

    public string Title { get; }

    public string Description { get; }

    public ObservableCollection<GridRow> Rows { get; } = [];

    /// <summary>Art grids, shown as pictures; they go into the game together with the rows.</summary>
    public ObservableCollection<GridRow> ArtRows { get; } = [];

    /// <summary>Asks which file to open; set by the view. Null when cancelled.</summary>
    public Func<Task<string?>>? PickOpenPath { get; set; }

    [ObservableProperty]
    private string? _accountText;

    [ObservableProperty]
    private string _catalogText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(RemoveAllCommand), nameof(AddFileCommand))]
    private bool _isBusy;

    bool IWorkingPage.IsWorking => IsBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private bool _isDownloading;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isOk;

    [ObservableProperty]
    private bool _isAttention;

    [ObservableProperty]
    private string? _note;

    /// <summary>Shown after a write into an account with Steam Cloud on.</summary>
    [ObservableProperty]
    private string? _cloudHint;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _canApplyNow;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveAllCommand))]
    private bool _canRemoveNow;

    public bool CanDownloadEver => _services.GridCatalog.CanDownload;

    private bool CanApply => CanApplyNow && !IsBusy;

    private bool CanRemove => CanRemoveNow && !IsBusy;

    private bool NotBusy => !IsBusy;

    private bool CanDownload => CanDownloadEver && !IsDownloading;

    public void Activated()
    {
        if (!IsBusy)
        {
            _ = RefreshAsync();
        }

        DateTimeOffset now = DateTimeOffset.Now;
        if (!IsDownloading && _services.GridCatalog.IsStale(now) && !(now - _downloadFailedAt < RetryAfter))
        {
            _ = DownloadAsync();
        }
    }

    /// <summary>Takes the layouts of a hero grid file and ticks them; they reach the game with the write button.</summary>
    internal async Task AddFromAsync(string path)
    {
        try
        {
            IReadOnlyList<HeroGridLayout> layouts = await Task.Run(() =>
            {
                var file = new FileInfo(path);
                return file.Length > GridLayouts.MaxFileBytes
                    ? throw new InvalidDataException($"{file.Length} bytes is too large for a hero grid file")
                    : GridLayouts.Read(File.ReadAllBytes(path));
            });
            string name = Path.GetFileName(path);
            foreach (HeroGridLayout layout in layouts)
            {
                _fromFiles[layout.Name] = (layout, name);
                _wishes[layout.Name] = true;
            }

            Note = string.Format(CultureInfo.CurrentCulture, Strings.GridFileAddedFormat, layouts.Count, name);
            Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _services.Log.Error($"Hero grid file {path} was not taken", ex);
            Note = string.Format(CultureInfo.CurrentCulture, Strings.GridFileProblemFormat, ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task ApplyAsync()
    {
        List<GridChoice> wanted = [.. Rows.Concat(ArtRows).Where(r => r.IsOn).Select(r => new GridChoice(r.Name, r.Layout))];
        return ChangeAsync(account => _services.HeroGrids.Apply(_services.ReadGame(), account, wanted));
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveAllAsync()
    {
        return ChangeAsync(account => _services.HeroGrids.RemoveAll(_services.ReadGame(), account));
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task AddFileAsync()
    {
        if (PickOpenPath is not null && await PickOpenPath() is string path)
        {
            await AddFromAsync(path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        IsDownloading = true;
        CatalogText = Strings.GridDownloading;
        try
        {
            _catalog = await _services.GridCatalog.RefreshAsync(CancellationToken.None);
            _catalogProblem = null;
            _downloadFailedAt = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _services.Log.Error("Hero grid layouts were not downloaded", ex);
            _catalogProblem = ex.Message;
            _downloadFailedAt = DateTimeOffset.Now;
        }
        finally
        {
            IsDownloading = false;
        }

        CatalogText = CatalogLine();
        Show();
    }

    private async Task ChangeAsync(Func<uint, GridResult> change)
    {
        if (_state?.Account is not { } account)
        {
            return;
        }

        bool cloud = _state.CloudEnabled;
        string accountText = AccountName(account);
        IsBusy = true;
        string? message;
        bool done = false;
        try
        {
            GridResult result = await Task.Run(() => change(account.AccountId));
            done = result.Refusal is null && result.Added + result.Removed > 0;
            message = RefusalText(result.Refusal, result.Detail)
                ?? string.Format(CultureInfo.CurrentCulture, Strings.GridDoneFormat, result.Added, result.Removed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _services.Log.Error("Change of the hero grids failed", ex);
            message = string.Format(CultureInfo.CurrentCulture, Strings.GridErrorFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (done)
        {
            _wishes.Clear();
        }

        await RefreshAsync();
        Note = message;
        CloudHint = done && cloud ? string.Format(CultureInfo.CurrentCulture, Strings.TransferCloudHintFormat, accountText) : null;
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            (_game, _state) = await Task.Run(ReadState);
            _installed = _state.Installed.DistinctBy(g => g.Name, StringComparer.Ordinal).ToDictionary(g => g.Name, StringComparer.Ordinal);
            Note = BaseNote;
            CloudHint = null;
            AccountText = _state.Account is { } account
                ? string.Format(CultureInfo.CurrentCulture, Strings.GridAccountFormat, AccountName(account))
                : null;
            Show();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private (GameState, GridAccountState) ReadState()
    {
        GameState game = _services.ReadGame();
        try
        {
            return (game, HeroGridService.Read(game));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Error("Hero grid file is unreadable", ex);
            return (game, new GridAccountState(GridRefusal.BrokenFile, ex.Message, game.Steam?.Account, false, false, []));
        }
    }

    /// <summary>Builds the list again and shows how the game relates to the ticks; the note is left to the caller.</summary>
    private void Show()
    {
        if (_state is null)
        {
            return;
        }

        Dictionary<string, InstalledGrid> installed = _installed;
        var rows = new List<GridRow>();
        var art = new List<GridRow>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (HeroGridLayout layout in _catalog.Layouts)
        {
            if (!_fromFiles.ContainsKey(layout.Name))
            {
                Add(rows, layout.Name, Contents(layout), layout);
            }
        }

        foreach ((string name, (HeroGridLayout layout, string file)) in _fromFiles)
        {
            Add(rows, name, string.Format(CultureInfo.CurrentCulture, Strings.GridFromFileFormat, file, Contents(layout)), layout);
        }

        foreach (HeroGridLayout layout in _art ??= ArtGrids.BuiltIn())
        {
            Add(art, layout.Name, Strings.GridPicture, layout);
        }

        foreach (InstalledGrid grid in installed.Values)
        {
            Add(rows, grid.Name, grid.Layout is null
                ? Strings.GridInGame
                : string.Format(CultureInfo.CurrentCulture, Strings.GridInGameFormat, Contents(grid.Layout)), null);
        }

        Rows.Clear();
        foreach (GridRow row in rows)
        {
            Rows.Add(row);
        }

        ArtRows.Clear();
        foreach (GridRow row in art)
        {
            ArtRows.Add(row);
        }

        ShowVerdict();

        void Add(List<GridRow> into, string name, string description, HeroGridLayout? layout)
        {
            if (names.Add(name))
            {
                bool on = _wishes.TryGetValue(name, out bool wish) ? wish : installed.ContainsKey(name);
                into.Add(new GridRow(name, description, layout, on, OnRowChanged));
            }
        }
    }

    private void ShowVerdict()
    {
        Dictionary<string, InstalledGrid> installed = _installed;
        bool open = _game?.DotaRunning != true;
        if (_state!.Problem is GridRefusal problem)
        {
            Summary = problem == GridRefusal.NoAccount && _state.Account is not null ? Strings.GridNotStarted : RefusalText(problem, _state.Detail)!;
            (IsOk, IsAttention, CanApplyNow, CanRemoveNow) = (false, true, false, false);
            return;
        }

        List<GridRow> on = [.. Rows.Concat(ArtRows).Where(r => r.IsOn)];
        bool sameNames = on.Count == installed.Count && on.All(r => installed.ContainsKey(r.Name));
        bool fresh = on.All(r => r.Layout is null || installed.GetValueOrDefault(r.Name)?.Layout?.SameAs(r.Layout) == true);
        bool matches = sameNames && fresh;
        // Only taking layouts out is left when the game cannot read the file yet.
        bool adds = !fresh || on.Any(r => !installed.ContainsKey(r.Name));
        Summary = matches
            ? installed.Count == 0 ? Strings.GridNoneInGame : Strings.GridMatches
            : sameNames ? Strings.GridNewer : Strings.GridDiffers;
        (IsOk, IsAttention) = (matches && installed.Count > 0, !matches);
        CanApplyNow = open && !matches && (_state.SeenByGame || !adds);
        CanRemoveNow = open && installed.Count > 0;
    }

    private void OnRowChanged(GridRow row)
    {
        _wishes[row.Name] = row.IsOn;
        if (_state is not null)
        {
            Note = BaseNote;
            CloudHint = null;
            ShowVerdict();
        }
    }

    private string CatalogLine()
    {
        string date = _catalog.Date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        string line = string.Format(CultureInfo.CurrentCulture,
            _catalog.Origin == CatalogOrigin.BuiltIn ? Strings.GridCatalogBuiltInFormat : Strings.GridCatalogDownloadedFormat, date);
        return _catalogProblem is null ? line : line + " " + string.Format(CultureInfo.CurrentCulture, Strings.GridDownloadFailedFormat, _catalogProblem);
    }

    private string? BaseNote => _game?.DotaRunning == true ? Strings.GridDotaRunning
        : _state is { Problem: null, SeenByGame: false } ? Strings.GridNotSeen
        : null;

    /// <summary>The names of the categories, which say more about a layout than any count.</summary>
    /// <remarks>
    /// Only categories that hold heroes: an art grid draws its picture with thousands of empty
    /// labelled ones, and such a layout is called a picture instead.
    /// </remarks>
    private static string Contents(HeroGridLayout layout)
    {
        List<string> names = [.. layout.Categories.Where(c => c.HeroIds.Count > 0 && c.Name.Length > 0).Select(c => c.Name).Distinct(StringComparer.Ordinal).Take(9)];
        return names.Count == 0 ? Strings.GridPicture
            : names.Count > 8 ? string.Join(", ", names.Take(8)) + ", ..."
            : string.Join(", ", names);
    }

    private static string AccountName(Core.Steam.SteamAccount account) => account.AccountName.Length == 0
        ? account.AccountId.ToString(CultureInfo.CurrentCulture)
        : string.Format(CultureInfo.CurrentCulture, Strings.TransferAccountFormat, account.AccountName, account.AccountId);

    private static string? RefusalText(GridRefusal? refusal, string? detail) => refusal switch
    {
        GridRefusal.DotaRunning => Strings.GridDotaRunning,
        GridRefusal.NoSteam => Strings.ProblemSteamNotFound,
        GridRefusal.NoAccount => Strings.GridNoAccount,
        GridRefusal.BrokenFile => string.Format(CultureInfo.CurrentCulture, Strings.GridBrokenFormat, detail),
        GridRefusal.NotSeenByGame => Strings.GridNotSeen,
        _ => null,
    };
}
