namespace DotaTune.ViewModels;

public enum SectionKind
{
    GameState,
    Effects,
    Clicks,
    MenuBackground,
    Mmr,
    AutoAccept,
    HeroGrid,
    Config,
    Reset,
    Settings,
}

/// <summary>A page that reads the game again each time it is opened, since another page may have changed it.</summary>
public interface IActivatedPage
{
    void Activated();
}

/// <summary>A page that may be writing the game files right now; an update waits for it to finish.</summary>
public interface IWorkingPage
{
    bool IsWorking { get; }
}

/// <summary>One entry of the side navigation and the page it opens.</summary>
public sealed class SectionViewModel(SectionKind kind, string title, object page)
{
    public SectionKind Kind { get; } = kind;

    public string Title { get; } = title;

    public object Page { get; } = page;
}
