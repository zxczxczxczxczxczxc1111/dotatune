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

/// <summary>One entry of the side navigation and the page it opens.</summary>
public sealed class SectionViewModel(SectionKind kind, string title, object page)
{
    public SectionKind Kind { get; } = kind;

    public string Title { get; } = title;

    public object Page { get; } = page;
}
