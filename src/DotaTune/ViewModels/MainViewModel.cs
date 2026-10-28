using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotaTune.Resources;
using DotaTune.Updates;

namespace DotaTune.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    internal MainViewModel(AppServices services, SettingsViewModel settings, SectionKind initial)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        Updates = services.Updates;
        News = services.News;
        // The colours page shows the minimap colours of the config page's choice.
        var config = new ConfigViewModel(services, Strings.NavConfig, Strings.ConfigDescription);
        Sections =
        [
            new SectionViewModel(SectionKind.GameState, Strings.NavGameState,
                new GameStateViewModel(services, Strings.NavGameState, Strings.GameStateDescription)),
            new SectionViewModel(SectionKind.Effects, Strings.NavEffects,
                new EffectsViewModel(services, Strings.NavEffects, Strings.EffectsDescription)),
            new SectionViewModel(SectionKind.Clicks, Strings.NavClicks,
                new ClicksViewModel(services, config, Strings.NavClicks, Strings.ClicksDescription)),
            new SectionViewModel(SectionKind.MenuBackground, Strings.NavMenuBackground,
                new MenuBackgroundViewModel(services, Strings.NavMenuBackground, Strings.MenuBackgroundDescription)),
            new SectionViewModel(SectionKind.Mmr, Strings.NavMmr,
                new MmrViewModel(services, Strings.NavMmr, Strings.MmrDescription)),
            new SectionViewModel(SectionKind.AutoAccept, Strings.NavAutoAccept,
                new AutoAcceptViewModel(services, Strings.NavAutoAccept, Strings.AutoAcceptDescription)),
            new SectionViewModel(SectionKind.HeroGrid, Strings.NavHeroGrid,
                new HeroGridViewModel(services, Strings.NavHeroGrid, Strings.HeroGridDescription)),
            new SectionViewModel(SectionKind.Config, Strings.NavConfig, config),
            new SectionViewModel(SectionKind.Reset, Strings.NavReset,
                new ResetViewModel(services, Strings.NavReset, Strings.ResetDescription)),
        ];
        SettingsSection = new SectionViewModel(SectionKind.Settings, Strings.NavSettings, settings);

        if (initial == SectionKind.Settings)
        {
            _current = SettingsSection;
        }
        else
        {
            _selectedNav = Sections.First(s => s.Kind == initial);
            _current = _selectedNav;
        }

        OnCurrentChanged(_current);
    }

    public IReadOnlyList<SectionViewModel> Sections { get; }

    public SectionViewModel SettingsSection { get; }

    /// <summary>The update panel over the pages; it outlives the window, see <see cref="UpdateService"/>.</summary>
    public UpdateService Updates { get; }

    /// <summary>The card with what is new after an update.</summary>
    public UpdateNews News { get; }

    /// <summary>A page is writing the game files or waits for its turn to.</summary>
    internal bool IsWorking => Sections.Any(s => s.Page is IWorkingPage { IsWorking: true });

    /// <summary>Selection of the main list; empty while settings are open.</summary>
    [ObservableProperty]
    private SectionViewModel? _selectedNav;

    /// <summary>Page shown on the right.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsOpen))]
    private SectionViewModel _current;

    public bool IsSettingsOpen => Current.Kind == SectionKind.Settings;

    public void Open(SectionKind kind)
    {
        if (kind == SectionKind.Settings)
        {
            OpenSettings();
        }
        else
        {
            SelectedNav = Sections.First(s => s.Kind == kind);
        }
    }

    partial void OnSelectedNavChanged(SectionViewModel? value)
    {
        if (value is not null)
        {
            Current = value;
        }
    }

    partial void OnCurrentChanged(SectionViewModel value) => (value.Page as IActivatedPage)?.Activated();

    [RelayCommand]
    private void OpenSettings()
    {
        Current = SettingsSection;
        SelectedNav = null;
    }

    /// <summary>"Later" and "Close" of the card; the page under it reads the game again, the pack may be new.</summary>
    [RelayCommand]
    private void CloseNews()
    {
        News.IsOpen = false;
        (Current.Page as IActivatedPage)?.Activated();
    }
}
