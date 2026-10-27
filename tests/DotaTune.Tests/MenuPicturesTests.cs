using System.Buffers.Binary;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Menu;
using DotaTune.Core.Presets;
using SkiaSharp;

namespace DotaTune.Tests;

public sealed class MenuPicturesTests : IDisposable
{
    private readonly CyrillicTempFolder _folder = new();
    private readonly MenuPictures _pictures;

    public MenuPicturesTests() =>
        _pictures = new MenuPictures(Path.Combine(_folder.Path, "Иван", "menu-pictures"), new FileLog(Path.Combine(_folder.Path, "logs")));

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData(400, 400, 400, 225)]
    [InlineData(4000, 2000, 1920, 1080)]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1280, 1024, 1280, 720)]
    [InlineData(3, 1, 2, 1)]
    public void Picture_is_cut_to_the_screen_shape(int width, int height, int expectedWidth, int expectedHeight)
    {
        byte[] png = MenuPictures.ToMenuPng(MenuGameFiles.Png(width, height));
        Assert.Equal((expectedWidth, expectedHeight), PanoramaTexture.PngSize(png));
    }

    [Fact]
    public void Huge_picture_is_refused_before_it_is_decoded()
    {
        // A small PNG whose header says 20000x10000: refused by its size, not after taking 800 MB.
        byte[] png = MenuGameFiles.Png(16, 9);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 20000);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 10000);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), Crc32.Compute(png.AsSpan(12, 17)));

        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => MenuPictures.ToMenuPng(png));
        Assert.Contains("megapixels", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Picture_keeps_the_centre_and_has_no_holes()
    {
        // Red bands left and right of a 16:9 middle, half transparent: the cut drops the bands and lays the rest on black.
        using var bitmap = new SKBitmap(260, 90);
        bitmap.Erase(new SKColor(255, 0, 0));
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Color = new SKColor(0, 0, 255, 128), BlendMode = SKBlendMode.Src };
            canvas.DrawRect(SKRect.Create(50, 0, 160, 90), paint);
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using SKBitmap result = SKBitmap.Decode(MenuPictures.ToMenuPng(data.ToArray()));

        Assert.Equal((160, 90), (result.Width, result.Height));
        foreach ((int x, int y) in new[] { (1, 1), (158, 88), (80, 45) })
        {
            SKColor pixel = result.GetPixel(x, y);
            Assert.Equal(255, pixel.Alpha);
            Assert.Equal(0, pixel.Red);
            Assert.InRange(pixel.Blue, 120, 136);
        }
    }

    [Fact]
    public void Not_a_picture_is_refused()
    {
        string file = Path.Combine(_folder.Path, "не картинка.png");
        File.WriteAllText(file, "text");
        Assert.Throws<InvalidDataException>(() => _pictures.Add(file));
        Assert.Empty(_pictures.Added());
    }

    [Fact]
    public void Picture_is_added_once_and_removed()
    {
        string file = Path.Combine(_folder.Path, "Арт паук.jpg");
        using (var bitmap = new SKBitmap(800, 600))
        using (SKImage image = SKImage.FromBitmap(bitmap))
        using (SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 90))
        {
            File.WriteAllBytes(file, data.ToArray());
        }

        string id = _pictures.Add(file);
        Assert.Equal(id, _pictures.Add(file));
        Assert.Equal([id], _pictures.Added());
        Assert.True(MenuPictures.IsAdded(id));
        Assert.Equal(MenuBackgroundKind.Picture, MenuPictures.KindOf(id));
        Assert.Equal((800, 450), PanoramaTexture.PngSize(_pictures.Png(id)));
        Assert.True(File.Exists(file));

        _pictures.Remove(id);
        Assert.Empty(_pictures.Added());
        Assert.True(File.Exists(file));
        Assert.Throws<FileNotFoundException>(() => _pictures.Png(id));
    }

    [Theory]
    [InlineData("picture:../settings.png")]
    [InlineData("picture:..\\settings.png")]
    [InlineData("picture:C:\\Windows\\a.png")]
    [InlineData("picture:a.txt")]
    [InlineData("picture:")]
    [InlineData("sample:unknown")]
    [InlineData("something")]
    public void Ids_outside_the_gallery_are_not_pictures(string id)
    {
        Assert.Throws<FileNotFoundException>(() => _pictures.Open(id));
        Assert.Throws<FileNotFoundException>(() => _pictures.Png(id));
    }

    [Fact]
    public void Samples_are_built_in_and_go_to_the_game_as_png()
    {
        Assert.Equal(MenuBackgroundKind.Game, MenuPictures.KindOf(null));
        Assert.Equal(MenuBackgroundKind.Black, MenuPictures.KindOf(MenuPictures.BlackId));
        foreach (string name in MenuPictures.Samples)
        {
            string id = MenuPictures.SampleId(name);
            Assert.False(MenuPictures.IsAdded(id));
            (int width, int height) = PanoramaTexture.PngSize(_pictures.Png(id)) ?? throw new InvalidDataException($"{name} did not come out as PNG");
            Assert.InRange(width, 640, MenuPictures.MaxWidth);
            Assert.InRange(height * 16, (width * 9) - 16, (width * 9) + 16);
        }
    }
}
