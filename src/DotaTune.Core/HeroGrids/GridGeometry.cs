namespace DotaTune.Core.HeroGrids;

/// <summary>A rectangle in the units of a layout.</summary>
public readonly record struct GridBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>
/// How the hero grid editor of the game lays a layout out, measured on stand frames at
/// 2560x1440 on 26.10.2026: the editor, the names of categories and the hero cards.
/// </summary>
public static class GridGeometry
{
    /// <summary>The part of the editor seen without scrolling, in layout units.</summary>
    public const double CanvasWidth = 1193;

    public const double CanvasHeight = 593;

    /// <summary>The editor draws a unit about 3.5% taller than it is wide.</summary>
    public const double VerticalStretch = 1.035;

    /// <summary>A category name is written in capitals of this size at the corner of its box.</summary>
    public const double LabelSize = 14.4;

    public const double LabelSpacing = 1.8;

    /// <summary>The room a category name takes above the cards; none without a name.</summary>
    public const double LabelRow = 17;

    /// <summary>Width to height of a hero card.</summary>
    public const double CardAspect = 0.69;

    /// <summary>
    /// The cards of a category in order: as large as the box lets them be, in as many rows as
    /// that takes, filled from the left. Cards never overflow the box, they shrink.
    /// </summary>
    public static IReadOnlyList<GridBox> Cards(HeroGridCategory category)
    {
        ArgumentNullException.ThrowIfNull(category);
        int count = category.HeroIds.Count;
        double label = category.Name.Length > 0 ? LabelRow : 0;
        double room = Math.Max(0, category.Height - label);
        if (count == 0 || category.Width <= 0 || room <= 0)
        {
            return [];
        }

        double width = 0;
        int columns = 1;
        for (int c = 1; c <= count; c++)
        {
            int rows = (count + c - 1) / c;
            double fits = Math.Min(category.Width / c, room / rows * CardAspect);
            if (fits > width)
            {
                (width, columns) = (fits, c);
            }
        }

        double height = width / CardAspect;
        var cards = new GridBox[count];
        for (int i = 0; i < count; i++)
        {
            cards[i] = new GridBox(category.X + (i % columns * width), category.Y + label + (i / columns * height), width, height);
        }

        return cards;
    }

    /// <summary>What a picture of the layout has to show: the editor, and whatever lies past its edges.</summary>
    public static GridBox Extent(HeroGridLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        double left = 0, top = 0, right = CanvasWidth, bottom = CanvasHeight;
        foreach (HeroGridCategory c in layout.Categories)
        {
            left = Math.Min(left, c.X);
            top = Math.Min(top, c.Y);
            right = Math.Max(right, c.X + c.Width);
            bottom = Math.Max(bottom, c.Y + Math.Max(c.Height, c.Name.Length > 0 ? LabelRow : 0));
        }

        return new GridBox(left, top, right - left, bottom - top);
    }
}
