using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DotaTune.Core.HeroGrids;
using DotaTune.Views;

namespace DotaTune.Tests;

public class GridPreviewTests
{
    private static readonly FontFamily Onest = new("avares://DotaTune/Assets/Fonts#Onest");

    [Fact]
    public void Cards_shrink_to_fit_their_category_as_in_the_game()
    {
        // Measured on the stand: an unnamed 527 x 61.9 box of 16 heroes is one row of cards 32.9 wide,
        // a named 1100 x 107.7 box of 12 heroes one row 90.7 high under the name.
        IReadOnlyList<GridBox> shizuku = GridGeometry.Cards(new HeroGridCategory("", 100, 50, 527, 61.9, [.. Enumerable.Range(1, 16)]));
        Assert.Equal(16, shizuku.Count);
        Assert.All(shizuku, c => Assert.Equal(50, c.Y));
        Assert.Equal(527 / 16.0, shizuku[0].Width, 3);
        Assert.Equal(100 + (15 * 527 / 16.0), shizuku[15].X, 3);

        IReadOnlyList<GridBox> stratz = GridGeometry.Cards(new HeroGridCategory("Carry", 0, 0, 1100, 107.7, [.. Enumerable.Range(1, 12)]));
        Assert.All(stratz, c => Assert.Equal(GridGeometry.LabelRow, c.Y));
        Assert.Equal(107.7 - GridGeometry.LabelRow, stratz[0].Height, 3);
        Assert.Equal(stratz[0].Height * GridGeometry.CardAspect, stratz[0].Width, 3);

        // A narrow tall box takes its heroes in rows and stays inside.
        IReadOnlyList<GridBox> column = GridGeometry.Cards(new HeroGridCategory("Mid", 10, 10, 60, 400, [1, 2, 3, 4, 5]));
        Assert.True(column.Select(c => c.Y).Distinct().Count() > 1);
        Assert.All(column, c => Assert.True(c.Right <= 70.001 && c.Bottom <= 410.001, $"{c} is outside the box"));

        Assert.Empty(GridGeometry.Cards(new HeroGridCategory("Ban", 0, 0, 100, 100, [])));
        Assert.Empty(GridGeometry.Cards(new HeroGridCategory("Ban", 0, 0, 100, 10, [1])));
    }

    [Fact]
    public void Picture_takes_the_editor_and_what_lies_past_it()
    {
        Assert.Equal(new GridBox(0, 0, GridGeometry.CanvasWidth, GridGeometry.CanvasHeight),
            GridGeometry.Extent(new HeroGridLayout("small", [new HeroGridCategory("a", 10, 10, 30, 30, [])])));
        Assert.Equal(new GridBox(-20, 0, 1320, 700),
            GridGeometry.Extent(new HeroGridLayout("wide", [new HeroGridCategory("a", -20, 600, 1320, 100, [])])));
    }

    [Fact]
    public void Art_grids_are_built_in()
    {
        IReadOnlyList<HeroGridLayout> art = ArtGrids.BuiltIn();

        Assert.Equal(["Красный стул", "пацанская сетка", "Hate people love larp", "Net v1", "support"], art.Select(l => l.Name));
        Assert.All(art, l => Assert.True(l.Categories.Count > 200, $"{l.Name} is not a picture"));
        Assert.Equal(3, art[0].Categories.Count(c => c.HeroIds.Count > 0));
    }

    [AvaloniaFact]
    public void Preview_draws_names_and_cards_where_they_stand()
    {
        var layout = new HeroGridLayout("test",
        [
            new HeroGridCategory("@", 20, 20, 30, 30, []),
            new HeroGridCategory("", 600, 300, 200, 200, [1, 2]),
        ]);

        using RenderTargetBitmap picture = GridPreview.Draw(layout, Onest);
        byte[] alpha = Alpha(picture);
        double scale = GridPreview.PictureSize.Width / GridGeometry.CanvasWidth;

        Assert.True(Covered(alpha, Box(20, 20, 15, 15, scale)) > 0, "the name is not drawn");
        Assert.True(Covered(alpha, Box(620, 340, 40, 80, scale)) > 0.9, "the card is not drawn");
        Assert.Equal(0, Covered(alpha, Box(200, 200, 200, 200, scale)));
    }

    [AvaloniaFact]
    public void Art_grid_pictures_are_drawn()
    {
        string dir = Path.Combine(RepoPaths.Root, "artifacts", "previews");
        Directory.CreateDirectory(dir);
        foreach ((HeroGridLayout layout, int i) in ArtGrids.BuiltIn().Select((l, i) => (l, i)))
        {
            using RenderTargetBitmap picture = GridPreview.Draw(layout, Onest);
            picture.Save(Path.Combine(dir, $"art-{i}.png"), new PngBitmapEncoderOptions());
            double drawn = Covered(Alpha(picture), new PixelRect(GridPreview.PictureSize));
            Assert.True(drawn is > 0.005 and < 0.5, $"{layout.Name}: {drawn:P1} of the picture is drawn");
        }
    }

    private static PixelRect Box(double x, double y, double width, double height, double scale) =>
        new((int)(x * scale), (int)(y * scale * GridGeometry.VerticalStretch), (int)(width * scale), (int)(height * scale * GridGeometry.VerticalStretch));

    /// <summary>The alpha of every pixel, row by row.</summary>
    private static byte[] Alpha(Bitmap picture)
    {
        PixelSize size = picture.PixelSize;
        int stride = size.Width * 4;
        byte[] pixels = new byte[stride * size.Height];
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            picture.CopyPixels(new PixelRect(size), pin.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            pin.Free();
        }

        byte[] alpha = new byte[size.Width * size.Height];
        for (int i = 0; i < alpha.Length; i++)
        {
            alpha[i] = pixels[(i * 4) + 3];
        }

        return alpha;
    }

    /// <summary>The share of pixels in the box that are not transparent.</summary>
    private static double Covered(byte[] alpha, PixelRect box)
    {
        int width = GridPreview.PictureSize.Width;
        int covered = 0;
        for (int y = box.Y; y < box.Bottom; y++)
        {
            for (int x = box.X; x < box.Right; x++)
            {
                covered += alpha[(y * width) + x] > 0 ? 1 : 0;
            }
        }

        return (double)covered / (box.Width * box.Height);
    }
}
