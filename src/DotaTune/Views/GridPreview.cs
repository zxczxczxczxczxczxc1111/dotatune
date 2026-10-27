using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using DotaTune.Core.HeroGrids;

namespace DotaTune.Views;

/// <summary>
/// A small picture of a hero grid layout as the editor of the game shows it: category names
/// in capitals where they stand, hero cards as plain tiles. Transparent, so the frame around
/// it gives the background.
/// </summary>
public sealed class GridPreview : Control
{
    public static readonly StyledProperty<HeroGridLayout?> LayoutProperty =
        AvaloniaProperty.Register<GridPreview, HeroGridLayout?>(nameof(Layout));

    /// <summary>Pixels of a picture: twice the size it is shown at, and the proportions of the editor.</summary>
    internal static readonly PixelSize PictureSize = new(800,
        (int)Math.Round(800 * GridGeometry.CanvasHeight * GridGeometry.VerticalStretch / GridGeometry.CanvasWidth));

    /// <summary>An art grid has thousands of names to lay out, so each layout is drawn once.</summary>
    private static readonly ConditionalWeakTable<HeroGridLayout, RenderTargetBitmap> Pictures = [];

    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush CardBrush = new SolidColorBrush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));

    static GridPreview() => AffectsRender<GridPreview>(LayoutProperty);

    public GridPreview() => RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);

    public HeroGridLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Layout is { } layout && Pictures.TryGetValue(layout, out RenderTargetBitmap? picture))
        {
            context.DrawImage(picture, new Rect(picture.Size), new Rect(Bounds.Size));
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LayoutProperty && change.NewValue is HeroGridLayout layout && !Pictures.TryGetValue(layout, out _))
        {
            // A picture takes tens of milliseconds: the page shows first, the pictures come one by one after it.
            Dispatcher.UIThread.Post(() => Prepare(layout), DispatcherPriority.Background);
        }
    }

    /// <summary>Takes the width it is given and the height of the editor's proportions.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : PictureSize.Width / 2.0;
        return new Size(width, width * PictureSize.Height / PictureSize.Width);
    }

    internal static RenderTargetBitmap Draw(HeroGridLayout layout, FontFamily font)
    {
        GridBox extent = GridGeometry.Extent(layout);
        double scale = Math.Min(PictureSize.Width / extent.Width, PictureSize.Height / (extent.Height * GridGeometry.VerticalStretch));
        // What lies past the editor shrinks the picture; it stays in the top left corner, as the editor scrolls from there.
        Matrix units = Matrix.CreateTranslation(-extent.X, -extent.Y) * Matrix.CreateScale(scale, scale * GridGeometry.VerticalStretch);
        var typeface = new Typeface(font, FontStyle.Normal, FontWeight.Bold);
        var labels = new Dictionary<string, TextLayout>(StringComparer.Ordinal);
        var picture = new RenderTargetBitmap(PictureSize);
        bool drawn = false;
        try
        {
            using DrawingContext context = picture.CreateDrawingContext();
            // Coloured edges of ClearType letters would show once the picture is scaled.
            using (context.PushTextOptions(new TextOptions { TextRenderingMode = TextRenderingMode.Antialias }))
            using (context.PushTransform(units))
            {
                foreach (HeroGridCategory category in layout.Categories)
                {
                    foreach (GridBox card in GridGeometry.Cards(category))
                    {
                        context.DrawRectangle(CardBrush, null, new Rect(card.X + 1, card.Y + 1, Math.Max(0, card.Width - 2), Math.Max(0, card.Height - 2)), 3, 3);
                    }

                    if (category.Name.Length > 0)
                    {
                        if (!labels.TryGetValue(category.Name, out TextLayout? label))
                        {
                            label = new TextLayout(category.Name.ToUpperInvariant(), typeface, GridGeometry.LabelSize, LabelBrush,
                                letterSpacing: GridGeometry.LabelSpacing);
                            labels.Add(category.Name, label);
                        }

                        label.Draw(context, new Point(category.X, category.Y));
                    }
                }
            }

            drawn = true;
        }
        finally
        {
            if (!drawn)
            {
                picture.Dispose();
            }

            foreach (TextLayout label in labels.Values)
            {
                label.Dispose();
            }
        }

        return picture;
    }

    private void Prepare(HeroGridLayout layout)
    {
        if (!ReferenceEquals(Layout, layout) || Pictures.TryGetValue(layout, out _))
        {
            return;
        }

        try
        {
            Pictures.AddOrUpdate(layout, Draw(layout, Font()));
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            // A card without its picture still works; a throw here would take the window down.
            App.Services.Log.Error($"Preview of hero grid {layout.Name} was not drawn", ex);
        }
    }

    private FontFamily Font() => this.TryFindResource("BodyFont", out object? font) && font is FontFamily family ? family : FontFamily.Default;
}
