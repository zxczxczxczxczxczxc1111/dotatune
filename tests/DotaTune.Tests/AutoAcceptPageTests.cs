using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotaTune.Core.Accept;
using DotaTune.Core.Game;
using DotaTune.Core.Platform;
using DotaTune.Resources;
using DotaTune.ViewModels;
using DotaTune.Views;
using ValvePak;

namespace DotaTune.Tests;

public class AutoAcceptPageTests
{
    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Switch_and_delay_reach_the_pack_and_another_delay_replaces_one_file(string language)
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        AutoAcceptTests.AddGameScript(steam);
        (MainWindow window, AutoAcceptViewModel page) = Open(folder, steam, language);

        Assert.False(page.IsOn);
        Assert.Equal(5, page.Seconds);
        Assert.Equal(language == "ru" ? "5 с" : "5 s", page.SecondsText);
        Assert.Equal(Strings.StateNoPack, page.Summary);
        Assert.False(page.CanApplyNow);

        page.IsOn = true;
        Assert.True(App.Services.Settings.Load().AutoAccept);
        Assert.Equal(Strings.AutoAcceptNoPack, page.Summary);
        Assert.Equal(Strings.InstallPack, page.ApplyText);
        Assert.True(page.ApplyCommand.CanExecute(null));

        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Format(Strings.AutoAcceptPackOnFormat, 5), page.Summary);
        Assert.True(page.IsOk);
        Assert.False(page.CanApplyNow);
        Assert.Equal(5, App.Services.Packs.Current!.AutoAccept);
        Assert.Contains("$.Schedule(5,", ScriptText(), StringComparison.Ordinal);
        using (Avalonia.Media.Imaging.WriteableBitmap? frame = WindowSnapshotTests.Capture(window))
        {
            frame!.Save(Path.Combine(RepoPaths.Root, "artifacts", "snapshots", $"{language}-AutoAccept-on.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }

        // Another delay, set on the slider: the script is replaced in the pack, every other file and the marker stay.
        Dictionary<string, byte[]> before = PackFiles();
        window.GetVisualDescendants().OfType<Slider>().Single().Value = 9;
        Assert.Equal(9, page.Seconds);
        Assert.Equal(9, App.Services.Settings.Load().AutoAcceptDelay);
        Assert.Equal(Format(Strings.AutoAcceptBuiltOnFormat, 5), page.Summary);
        Assert.True(page.IsAttention);
        Assert.Equal(Strings.AutoAcceptUpdatePack, page.ApplyText);
        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Format(Strings.AutoAcceptPackOnFormat, 9), page.Summary);
        Assert.Null(page.Note);
        Assert.Equal(9, App.Services.Packs.Current!.AutoAccept);
        Assert.Contains("$.Schedule(9,", ScriptText(), StringComparison.Ordinal);
        Dictionary<string, byte[]> after = PackFiles();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        Assert.All(before.Where(f => f.Key != AutoAccept.ScriptPath), f => Assert.Equal(f.Value, after[f.Key]));

        // Off: the pack is built again without the script, so the window is the game's own.
        page.IsOn = false;
        Assert.False(App.Services.Settings.Load().AutoAccept);
        Assert.Equal(9, App.Services.Settings.Load().AutoAcceptDelay);
        Assert.Equal(Format(Strings.AutoAcceptBuiltOnFormat, 9), page.Summary);
        Assert.Equal(Strings.RebuildPack, page.ApplyText);
        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.AutoAcceptPackOff, page.Summary);
        Assert.False(page.IsOk);
        Assert.False(page.IsAttention);
        Assert.Null(App.Services.Packs.Current!.AutoAccept);
        Assert.False(PackFiles().ContainsKey(AutoAccept.ScriptPath));

        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaFact]
    public void Game_without_the_window_script_gets_no_auto_accept()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        (MainWindow window, AutoAcceptViewModel page) = Open(folder, steam, "ru");

        page.IsOn = true;
        Assert.Equal(Strings.AutoAcceptNotSupported, page.Summary);
        Assert.True(page.IsAttention);
        Assert.False(page.CanApplyNow);

        // A pack put in from another page leaves the window alone.
        App.Services.InstallPack();
        page.Activated();
        Settle(page);
        Assert.Null(App.Services.Packs.Current!.AutoAccept);
        Assert.False(PackFiles().ContainsKey(AutoAccept.ScriptPath));
        Assert.Equal(Strings.AutoAcceptNotSupported, page.Summary);
        Assert.False(page.CanApplyNow);

        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaFact]
    public void Changed_pack_is_built_again_instead_of_getting_another_delay()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        AutoAcceptTests.AddGameScript(steam);
        (MainWindow window, AutoAcceptViewModel page) = Open(folder, steam, "ru");
        page.IsOn = true;
        page.ApplyCommand.Execute(null);
        Settle(page);

        string pak = App.Services.Packs.Current!.PakPath;
        File.AppendAllText(pak, "x");
        page.Seconds = 12;
        page.Activated();
        Settle(page);
        Assert.Equal(Strings.HealthChanged, page.Summary);
        Assert.Equal(Strings.RebuildPack, page.ApplyText);
        Assert.False(App.Services.ReplaceAutoAccept(12));
        Assert.Equal(5, App.Services.Packs.Current!.AutoAccept);

        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Format(Strings.AutoAcceptPackOnFormat, 12), page.Summary);
        Assert.Contains("$.Schedule(12,", ScriptText(), StringComparison.Ordinal);

        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    [AvaloniaFact]
    public void Delay_that_could_not_be_replaced_is_told()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        AutoAcceptTests.AddGameScript(steam);
        (MainWindow window, AutoAcceptViewModel page) = Open(folder, steam, "ru");
        page.IsOn = true;
        page.ApplyCommand.Execute(null);
        Settle(page);

        // Dota starts after the page was read: the pack is held open and keeps its delay.
        page.Seconds = 3;
        Assert.Equal(Strings.AutoAcceptUpdatePack, page.ApplyText);
        steam.Running.Add("dota2");
        page.ApplyCommand.Execute(null);
        Settle(page);
        Assert.Equal(Strings.AutoAcceptNotReplaced, page.Note);
        Assert.Equal(5, App.Services.Packs.Current!.AutoAccept);
        Assert.False(page.CanApplyNow);

        steam.Running.Remove("dota2");
        App.Services.Packs.Remove(steam.Read());
        window.Close();
    }

    private static string Format(string format, int seconds) => string.Format(CultureInfo.CurrentCulture, format, seconds);

    private static (MainWindow, AutoAcceptViewModel) Open(CyrillicTempFolder folder, FakeSteam steam, string language)
    {
        App.Services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), steam.Read);
        UiLanguage.Apply(UiLanguage.Resolve(language));
        MainWindow window = App.CreateMainWindow(SectionKind.AutoAccept);
        window.Show();
        var main = (MainViewModel)window.DataContext!;
        var page = (AutoAcceptViewModel)main.Sections.Single(s => s.Kind == SectionKind.AutoAccept).Page;
        Settle(page);
        return (window, page);
    }

    private static string ScriptText() => AutoAcceptTests.Text(PackFiles()[AutoAccept.ScriptPath]);

    /// <summary>Every file of the installed pack, the marker too.</summary>
    private static Dictionary<string, byte[]> PackFiles()
    {
        using var package = new Package();
        package.Read(App.Services.Packs.Current!.PakPath);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (PackageEntry entry in package.Entries?.Values.SelectMany(e => e) ?? [])
        {
            package.ReadEntry(entry, out byte[] data);
            files[entry.GetFullPath()] = data;
        }

        Assert.True(files.ContainsKey(LanguageFolders.MarkerPath));
        return files;
    }

    private static void Settle(AutoAcceptViewModel page)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (page.IsBusy && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.False(page.IsBusy, "the auto accept page did not settle in 10 s");
    }
}
