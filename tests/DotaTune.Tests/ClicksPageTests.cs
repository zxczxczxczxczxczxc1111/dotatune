using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotaTune.Core.Config;
using DotaTune.Core.Effects;
using DotaTune.Core.Platform;
using DotaTune.Resources;
using DotaTune.ViewModels;
using DotaTune.Views;
using ValvePak;

namespace DotaTune.Tests;

public class ClicksPageTests
{
    private const string Arrows = "particles/ui_mouseactions/clicked_occlusion_basemove_arrows.vpcf_c";

    [AvaloniaFact]
    public void Choice_of_colours_is_saved()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, ClicksViewModel page) = Open(folder, steam);

        Assert.Equal(Enum.GetValues<MouseEffect>().Length, page.Rows.Count);
        Assert.All(page.Rows, r => Assert.False(r.IsOn));
        Assert.Equal(Strings.ColorsAsInGame, page.Summary);
        Assert.False(page.ApplyCommand.CanExecute(null));

        Row(page, MouseEffect.MoveClick).IsOn = true;
        Assert.Equal(new MouseColorChoice { MoveClick = new Rgb(160, 96, 255) }, App.Services.Settings.Load().MouseColors);
        Assert.Equal(Strings.ColorsNotApplied, page.Summary);
        // The first pack carries the effects too, so the button says it is the pack that goes in.
        Assert.Equal(Strings.InstallPack, page.ApplyText);
        Assert.True(page.ApplyCommand.CanExecute(null));

        Row(page, MouseEffect.MoveClick).Hex = "#ff0000";
        Row(page, MouseEffect.SelectionRings).IsOn = true;
        Row(page, MouseEffect.SelectionRings).SelectedSwatch = Row(page, MouseEffect.SelectionRings).Swatches[^1];
        Assert.Equal(
            new MouseColorChoice { MoveClick = new Rgb(255, 0, 0), SelectionRings = new Rgb(255, 255, 255) },
            App.Services.Settings.Load().MouseColors);

        // Every row back to the game's colour leaves nothing in the settings.
        Row(page, MouseEffect.MoveClick).IsOn = false;
        Row(page, MouseEffect.SelectionRings).IsOn = false;
        Assert.Null(App.Services.Settings.Load().MouseColors);
        Assert.Equal(Strings.ColorsAsInGame, page.Summary);
        Assert.False(page.ApplyCommand.CanExecute(null));
        window.Close();
    }

    [AvaloniaFact]
    public void Own_colour_is_picked_in_the_palette_and_saved_when_it_closes()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, ClicksViewModel page) = Open(folder, steam);
        ColorRow row = Row(page, MouseEffect.MoveClick);
        row.IsOn = true;
        Dispatcher.UIThread.RunJobs();
        Button pick = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("pick") && b.DataContext == row);
        Assert.DoesNotContain("custom", pick.Classes);

        pick.Flyout!.ShowAt(pick);
        Dispatcher.UIThread.RunJobs();
        ColorSpectrum spectrum = ((Control)((Flyout)pick.Flyout).Content!).GetVisualDescendants().OfType<ColorSpectrum>().Single();
        Assert.Equal(row.Color, RgbOf(spectrum.HsvColor));

        // A drag goes through many colours; none of them is saved while the palette is open.
        spectrum.HsvColor = HsvColor.FromAhsv(1, 120, 0.5, 0.5);
        spectrum.HsvColor = HsvColor.FromAhsv(1, 200, 0.8, 0.9);
        Rgb picked = RgbOf(HsvColor.FromAhsv(1, 200, 0.8, 0.9));
        Assert.Equal(picked, Rgb.ParseHex(row.PickerHex));
        Assert.Equal(new Rgb(160, 96, 255), App.Services.Settings.Load().MouseColors!.MoveClick);
        Snapshot(window, "ru-Clicks-picker.png");

        pick.Flyout.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(picked, App.Services.Settings.Load().MouseColors!.MoveClick);
        Assert.Equal(picked, Rgb.ParseHex(row.Hex));
        Assert.Null(row.SelectedSwatch);
        Assert.Contains("custom", pick.Classes);
        Snapshot(window, "ru-Clicks-own-colour.png");

        // A swatch chosen afterwards is what the palette opens with next time.
        row.SelectedSwatch = row.Swatches[^1];
        Assert.Equal(new Rgb(255, 255, 255), RgbOf(row.PickerColor));
        Assert.DoesNotContain("custom", pick.Classes);
        window.Close();
    }

    [AvaloniaFact]
    public void Palette_keeps_the_hue_on_white_and_takes_a_typed_code()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, ClicksViewModel page) = Open(folder, steam);
        ColorRow row = Row(page, MouseEffect.AttackClick);

        row.PickerColor = HsvColor.FromAhsv(1, 200, 0, 1);
        Assert.Equal(new Rgb(255, 255, 255), Rgb.ParseHex(row.PickerHex));
        Assert.Equal(200, row.PickerColor.H, 3);

        row.PickerHex = "#00ff0";
        Assert.Equal(200, row.PickerColor.H, 3);
        row.PickerHex = "#00ff00";
        Assert.Equal(new Rgb(0, 255, 0), RgbOf(row.PickerColor));

        // The row itself changes only when the palette hands its colour over.
        Assert.Null(App.Services.Settings.Load().MouseColors);

        // A half typed code does not stay in the palette after it closes.
        row.PickerHex = "#00ff0";
        row.TakePicked();
        Assert.Equal(new Rgb(0, 255, 0), row.Color);
        Assert.Equal(new Rgb(0, 255, 0), Rgb.ParseHex(row.PickerHex));
        window.Close();
    }

    [Fact]
    public void Colour_survives_the_way_through_the_palette()
    {
        // Opening and closing the palette goes RGB to HSV and back; no colour may move by it.
        var moved = new List<Rgb>();
        for (int r = 0; r < 256; r += 3)
        {
            for (int g = 0; g < 256; g += 3)
            {
                for (int b = 0; b < 256; b += 3)
                {
                    var color = new Rgb((byte)r, (byte)g, (byte)b);
                    if (RgbOf(Color.FromRgb(color.R, color.G, color.B).ToHsv()) != color)
                    {
                        moved.Add(color);
                    }
                }
            }
        }

        Assert.Empty(moved);
    }

    [AvaloniaFact]
    public void Palette_spectrum_shows_no_english_colour_names()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, ClicksViewModel page) = Open(folder, steam);
        ColorRow row = Row(page, MouseEffect.MoveClick);
        row.IsOn = true;
        Dispatcher.UIThread.RunJobs();
        Button pick = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("pick") && b.DataContext == row);

        pick.Flyout!.ShowAt(pick);
        Dispatcher.UIThread.RunJobs();
        ColorSpectrum spectrum = ((Control)((Flyout)pick.Flyout).Content!).GetVisualDescendants().OfType<ColorSpectrum>().Single();
        Control marker = spectrum.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_SelectionEllipsePanel");

        Assert.False(ToolTip.GetServiceEnabled(marker));
        pick.Flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public void Minimap_colours_are_written_from_the_colours_page_without_a_pack()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, ClicksViewModel page) = Open(folder, steam);
        var main = (MainViewModel)window.DataContext!;
        var config = (ConfigViewModel)main.Sections.Single(s => s.Kind == SectionKind.Config).Page;

        Assert.Same(config.Colors, page.MinimapRows);
        page.MinimapRows[1].IsOn = true;
        Assert.Equal(new Rgb(255, 0, 255), App.Services.Settings.Load().Config!.EnemyColor);
        Assert.Equal(Strings.ColorsNotApplied, page.Summary);
        Assert.Equal(Strings.ColorsApply, page.ApplyText);
        Assert.True(page.IsAttention);

        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(["dota_enemy_color 255 0 255"], AutoexecText.BlockLines(File.ReadAllText(AutoexecService.PathIn(steam.GameRoot)))!.Select(l => l.ToString()));
        Assert.Null(App.Services.Packs.Current);
        Assert.Equal(Strings.ColorsApplied, page.Summary);
        Assert.True(page.IsOk);
        Assert.False(page.CanApplyNow);
        Snapshot(window, "ru-Clicks-minimap.png");

        // The config page sees the written block, and its own change does not touch the colours.
        main.Open(SectionKind.Config);
        PumpUntil(() => !config.IsBusy);
        Assert.Equal(Strings.ConfigWritten, config.Summary);
        config.HideFrontpageVideo = true;
        Assert.Equal(Strings.ColorsApplied, page.Summary);

        // Closing Dota is the only way forward while it runs.
        steam.Running.Add("dota2");
        main.Open(SectionKind.Clicks);
        Settle(page);
        page.MinimapRows[1].IsOn = false;
        Assert.Equal(Strings.ColorsDotaRunning, page.Note);
        Assert.False(page.CanApplyNow);
        window.Close();
    }

    [AvaloniaFact]
    public void Pack_carries_the_recoloured_particles_and_is_rebuilt_on_a_change()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        GameFiles.WritePak(steam.Pak01, new Dictionary<string, byte[]>(GameFiles.Default().Concat(MouseColorTests.MouseParticles())));
        (MainWindow window, ClicksViewModel page) = Open(folder, steam);

        Row(page, MouseEffect.MoveClick).IsOn = true;
        Row(page, MouseEffect.HeroHighlight).IsOn = true;
        page.ApplyCommand.Execute(null);
        Settle(page);

        // The test game has no hero highlight: the pack matches the choice, nothing to rebuild, and the page says what did not change.
        var chosen = new MouseColorChoice { MoveClick = new Rgb(160, 96, 255), HeroHighlight = new Rgb(255, 255, 0) };
        Assert.Equal(chosen, App.Services.Packs.Current!.MouseColors);
        Assert.Equal([MouseEffect.HeroHighlight], App.Services.Packs.Current!.MouseNotTaken!);
        Assert.Equal(Strings.ColorsApplied, page.Summary);
        Assert.False(page.CanApplyNow);
        string notTaken = string.Format(CultureInfo.CurrentCulture, Strings.ClicksNotTakenFormat, Strings.ClicksHero);
        Assert.Equal(notTaken, page.Note);
        page.Activated();
        Settle(page);
        Assert.Equal(notTaken, page.Note);
        Kv3Value arrows = Particle(App.Services.Packs.Current!.PakPath, Arrows)!;
        // The arrows' green (76, 193, 82) becomes the purple at the same brightness.
        Assert.Equal([121.0, 73, 193, 255], arrows["m_ConstantColor"]!.Numbers!);

        Row(page, MouseEffect.HeroHighlight).IsOn = false;
        Assert.Equal(Strings.ColorsNotApplied, page.Summary);
        Assert.Null(page.Note);
        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.ColorsApplied, page.Summary);
        Assert.True(page.IsOk);
        Assert.Null(App.Services.Packs.Current!.MouseNotTaken);

        // Back to the game's colours: the rebuilt pack has none of the particles.
        Row(page, MouseEffect.MoveClick).IsOn = false;
        Assert.Equal(Strings.ColorsNotApplied, page.Summary);
        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.ColorsAsInGame, page.Summary);
        Assert.Null(App.Services.Packs.Current!.MouseColors);
        Assert.Null(Particle(App.Services.Packs.Current!.PakPath, Arrows));
        Assert.Null(page.Note);

        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    private static (MainWindow, ClicksViewModel) Open(CyrillicTempFolder folder, FakeSteam steam)
    {
        App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);
        UiLanguage.Apply(UiLanguage.Resolve("ru"));
        MainWindow window = App.CreateMainWindow(SectionKind.Clicks);
        window.Show();
        var main = (MainViewModel)window.DataContext!;
        var page = (ClicksViewModel)main.Sections.Single(s => s.Kind == SectionKind.Clicks).Page;
        Settle(page);
        return (window, page);
    }

    private static ColorRow Row(ClicksViewModel page, MouseEffect effect) => page.Rows[(int)effect];

    private static Rgb RgbOf(HsvColor color)
    {
        Color rgb = color.ToRgb();
        return new Rgb(rgb.R, rgb.G, rgb.B);
    }

    /// <summary>The root of a particle in the installed pack; null when the pack does not have it.</summary>
    private static Kv3Value? Particle(string pak, string path)
    {
        using var package = new Package();
        package.Read(pak);
        if (package.FindEntry(path) is not { } entry)
        {
            return null;
        }

        package.ReadEntry(entry, out byte[] bytes);
        return Kv3Binary.Read(CompiledResource.Data(bytes)).Root;
    }

    private static void Settle(ClicksViewModel page) => PumpUntil(() => !page.IsBusy);

    private static void PumpUntil(Func<bool> done)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), "the page did not settle in 10 s");
    }

    private static void Snapshot(MainWindow window, string name)
    {
        for (int i = 0; i < 4; i++)
        {
            Thread.Sleep(100);
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        using Avalonia.Media.Imaging.WriteableBitmap? frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        string dir = Path.Combine(RepoPaths.Root, "artifacts", "snapshots");
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
