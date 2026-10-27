extern alias setup;

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotaTune.Core.Diagnostics;
using setup::DotaTune.Setup;
using setup::DotaTune.Setup.Resources;

namespace DotaTune.Tests;

public partial class SetupTests
{
    private const string Nuspec = """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
        <metadata>
        <id>DotaTune</id>
        <version>{0}</version>
        <mainExe>DotaTune.exe</mainExe>
        </metadata>
        </package>
        """;

    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0+68277fca", "0.1.0")]
    [InlineData("0.2.0-beta.1", "0.2.0")]
    [InlineData(" 0.1 ", "0.1.0")]
    [InlineData("1.2.3.4", "1.2.3")]
    [InlineData("dota", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Version_text_is_read_as_three_parts(string? text, string? expected) =>
        Assert.Equal(expected, Installation.Parse(text)?.ToString());

    [Fact]
    public void Installer_version_is_the_build_version()
    {
        string props = XDocument.Load(Path.Combine(RepoPaths.Root, "Directory.Build.props")).Descendants("Version").Single().Value;
        Assert.Equal(Version.Parse(props), Installation.ThisVersion());
    }

    [Fact]
    public void Installed_version_is_read_from_the_velopack_manifest()
    {
        using var folder = new CyrillicTempFolder();
        string root = Path.Combine(folder.Path, "Иван", "DotaTune");
        var log = new FileLog(Path.Combine(folder.Path, "logs"));

        Assert.Equal(new InstalledApp(false, null), Installation.Find(root, log));
        Directory.CreateDirectory(root);
        Assert.Equal(new InstalledApp(false, null), Installation.Find(root, log));

        // Velopack overwrites any folder with files in it, so leftovers count as an installation.
        File.WriteAllText(Path.Combine(root, "velopack.log"), "x");
        Assert.Equal(new InstalledApp(true, null), Installation.Find(root, log));

        Directory.CreateDirectory(Path.Combine(root, "current"));
        File.WriteAllText(Path.Combine(root, "current", "sq.version"), string.Format(System.Globalization.CultureInfo.InvariantCulture, Nuspec, "0.1.0"));
        Assert.Equal(new InstalledApp(true, new Version(0, 1, 0)), Installation.Find(root, log));

        File.WriteAllText(Path.Combine(root, "current", "sq.version"), "<package><metadata>");
        Assert.Equal(new InstalledApp(true, null), Installation.Find(root, log));
        Assert.Contains("Installed version not read", File.ReadAllText(log.CurrentFile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, null, SetupCase.Fresh)]
    [InlineData(true, null, SetupCase.Unknown)]
    [InlineData(true, "0.0.9", SetupCase.Update)]
    [InlineData(true, "0.1.0", SetupCase.Same)]
    [InlineData(true, "0.10.0", SetupCase.Newer)]
    public void Case_follows_the_installed_version(bool present, string? installed, SetupCase expected) =>
        Assert.Equal(expected, Installation.Decide(new InstalledApp(present, installed is null ? null : Version.Parse(installed)), new Version(0, 1, 0)));

    [AvaloniaFact]
    public void Fresh_install_runs_velopack_and_opens_the_program()
    {
        using var scope = new CultureScope("ru-RU");
        using var folder = new CyrillicTempFolder();
        var steps = new FakeSteps();
        SetupViewModel model = Model(SetupCase.Fresh, null, steps, folder);
        bool finished = false;
        model.Finished += () => finished = true;

        Assert.Equal(SetupStrings.SetupFreshTitle, model.Title);
        Assert.Equal("Версия 0.2.0. Когда установка закончится, откроется окно DotaTune.", model.Lead);
        Assert.Equal(SetupStrings.SetupInstall, model.PrimaryText);
        Assert.Null(model.SecondaryText);
        Assert.Null(model.Note);

        model.PrimaryCommand.Execute(null);
        Settle(model);

        Assert.Equal(["install", "open"], steps.Calls);
        Assert.True(finished);
    }

    [AvaloniaFact]
    public void Update_closes_the_running_program_first_and_the_window_waits_for_velopack()
    {
        using var scope = new CultureScope("ru-RU");
        using var folder = new CyrillicTempFolder();
        var install = new TaskCompletionSource<int>();
        var steps = new FakeSteps { Running = true, Install = install.Task };
        SetupViewModel model = Model(SetupCase.Update, new Version(0, 1, 0), steps, folder);
        SetupWindow window = SetupWindow.Open(model);
        window.Show();

        Assert.Equal(SetupStrings.SetupUpdateTitle, model.Title);
        Assert.Equal("Сейчас стоит 0.1.0, обновится до 0.2.0. Настройки останутся.", model.Lead);
        Assert.Equal(SetupStrings.SetupUpdate, model.PrimaryText);
        Assert.Equal(SetupStrings.SetupAppRunning, model.Note);

        model.PrimaryCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.IsBusy);
        Assert.False(model.ShowButtons);
        Assert.Equal("Обновление DotaTune до 0.2.0", model.Title);
        Assert.Equal(SetupStrings.SetupWorking, model.Lead);
        Assert.Null(model.Note);
        Assert.False(model.PrimaryCommand.CanExecute(null));
        Assert.Equal(["close", "install"], steps.Calls);
        Snapshot(window, "ru-Setup-busy.png");

        // The close button does nothing until Velopack is done.
        window.Close();
        Assert.True(window.IsVisible);

        install.SetResult(0);
        Settle(model);
        Assert.Equal(["close", "install", "open"], steps.Calls);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Same_version_opens_the_program_and_reinstalls_on_the_second_button()
    {
        using var scope = new CultureScope("ru-RU");
        using var folder = new CyrillicTempFolder();
        var steps = new FakeSteps { Running = true };
        SetupViewModel model = Model(SetupCase.Same, new Version(0, 2, 0), steps, folder);
        int finished = 0;
        model.Finished += () => finished++;

        // The main button opens the running program, so nothing is said about closing it.
        Assert.Null(model.Note);
        steps.Running = false;
        Assert.Equal("DotaTune 0.2.0 уже стоит", model.Title);
        Assert.Equal(SetupStrings.SetupOpen, model.PrimaryText);
        Assert.Equal(SetupStrings.SetupReinstall, model.SecondaryText);

        model.PrimaryCommand.Execute(null);
        Assert.Equal(["open"], steps.Calls);
        Assert.Equal(1, finished);

        model.SecondaryCommand.Execute(null);
        Settle(model);
        Assert.Equal(["open", "install", "open"], steps.Calls);
        Assert.Equal(2, finished);
    }

    [AvaloniaFact]
    public void Newer_installed_version_is_kept_unless_asked()
    {
        using var scope = new CultureScope("ru-RU");
        using var folder = new CyrillicTempFolder();
        SetupViewModel model = Model(SetupCase.Newer, new Version(0, 3, 0), new FakeSteps(), folder);

        Assert.Equal(SetupStrings.SetupNewerTitle, model.Title);
        Assert.Equal("Сейчас стоит 0.3.0, а в этом установщике 0.2.0.", model.Lead);
        Assert.Equal(SetupStrings.SetupOpen, model.PrimaryText);
        Assert.Equal("Поставить 0.2.0", model.SecondaryText);
    }

    [AvaloniaTheory]
    [InlineData(SetupCase.Fresh, 1, false)]
    [InlineData(SetupCase.Update, 1, true)]
    [InlineData(SetupCase.Update, 0, true)]
    [InlineData(SetupCase.Update, SetupSteps.StillRunning, false)]
    public void Failed_install_points_to_the_log(SetupCase setupCase, int code, bool oldKept)
    {
        using var scope = new CultureScope("ru-RU");
        using var folder = new CyrillicTempFolder();
        var steps = new FakeSteps
        {
            // Code 0 stands for an exception before Velopack gave any code.
            Install = code == 0 ? Task.FromException<int>(new IOException("disk is full")) : Task.FromResult(code),
        };
        SetupViewModel model = Model(setupCase, setupCase == SetupCase.Fresh ? null : new Version(0, 1, 0), steps, folder);
        bool finished = false;
        model.Finished += () => finished = true;

        model.PrimaryCommand.Execute(null);
        Settle(model);

        Assert.True(model.IsFailed);
        Assert.Equal(SetupStrings.SetupFailedTitle, model.Title);
        Assert.Equal(oldKept ? SetupStrings.SetupFailedKeptLead : SetupStrings.SetupFailedLead, model.Lead);
        Assert.Equal(SetupStrings.SetupOpenLogs, model.PrimaryText);
        Assert.Equal(SetupStrings.SetupClose, model.SecondaryText);
        Assert.DoesNotContain("open", steps.Calls);

        model.PrimaryCommand.Execute(null);
        Assert.Equal("logs", steps.Calls[^1]);
        Assert.False(finished);
        model.SecondaryCommand.Execute(null);
        Assert.True(finished);
    }

    [AvaloniaFact]
    public void Program_that_does_not_open_after_the_install_is_reported()
    {
        using var scope = new CultureScope("ru-RU");
        using var folder = new CyrillicTempFolder();
        var steps = new FakeSteps { Opens = false };
        SetupViewModel model = Model(SetupCase.Fresh, null, steps, folder);

        model.PrimaryCommand.Execute(null);
        Settle(model);

        Assert.False(model.IsFailed);
        Assert.Equal(SetupStrings.SetupNotOpenedTitle, model.Title);
        Assert.Equal(SetupStrings.SetupNotOpenedLead, model.Lead);
        Assert.Equal(SetupStrings.SetupClose, model.PrimaryText);
        Assert.Equal(SetupStrings.SetupOpenLogs, model.SecondaryText);
    }

    [AvaloniaFact]
    public void Installer_built_without_the_package_says_so()
    {
        using var scope = new CultureScope("ru-RU");
        using var folder = new CyrillicTempFolder();
        SetupViewModel model = Model(SetupCase.Update, new Version(0, 1, 0), new FakeSteps { HasPayload = false }, folder);

        Assert.True(model.IsFailed);
        Assert.Equal(SetupStrings.SetupNoPayload, model.Lead);
        Assert.Equal(SetupStrings.SetupClose, model.PrimaryText);
        Assert.Null(model.SecondaryText);
    }

    public static TheoryData<string, string> Windows()
    {
        var data = new TheoryData<string, string>();
        foreach (string language in new[] { "ru", "en" })
        {
            foreach (string state in new[] { "fresh", "update", "same", "newer", "unknown", "failed" })
            {
                data.Add(language, state);
            }
        }

        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Windows))]
    public void Window_renders_and_fits(string language, string state)
    {
        using var scope = new CultureScope(language == "ru" ? "ru-RU" : "en-US");
        using var folder = new CyrillicTempFolder();
        var steps = new FakeSteps { Running = state == "update", Install = Task.FromResult(1) };
        SetupViewModel model = state switch
        {
            "fresh" => Model(SetupCase.Fresh, null, steps, folder),
            "update" => Model(SetupCase.Update, new Version(0, 1, 0), steps, folder),
            "same" => Model(SetupCase.Same, new Version(0, 2, 0), steps, folder),
            "newer" => Model(SetupCase.Newer, new Version(0, 10, 0), steps, folder),
            "unknown" => Model(SetupCase.Unknown, null, steps, folder),
            _ => Model(SetupCase.Update, new Version(0, 1, 0), steps, folder),
        };
        SetupWindow window = SetupWindow.Open(model);
        window.Show();
        if (state == "failed")
        {
            model.PrimaryCommand.Execute(null);
            Settle(model);
        }

        Snapshot(window, $"{language}-Setup-{state}.png");

        foreach (TextBlock block in window.GetLogicalDescendants().OfType<TextBlock>().Where(b => b.IsEffectivelyVisible))
        {
            Assert.True(
                block.DesiredSize.Width <= block.Bounds.Width + 0.5,
                $"\"{block.Text}\" needs {block.DesiredSize.Width:F0} px but got {block.Bounds.Width:F0}");
        }

        // A hovered pill grows to 1.04: nothing around it in this window clips.
        foreach (Button pill in window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("pill") && b.IsEffectivelyVisible))
        {
            Assert.DoesNotContain(pill.GetVisualAncestors(), v => v.ClipToBounds && v is not Window);
        }

        Image icon = window.GetVisualDescendants().OfType<Image>().Single();
        Assert.True(icon.Source is Bitmap { PixelSize.Width: >= 96 }, "the icon file did not give a large enough picture");
        window.Close();
    }

    [Fact]
    public void Installer_palette_matches_the_program_and_covers_every_resource_it_uses()
    {
        Dictionary<string, string> program = Brushes(Path.Combine(RepoPaths.Root, "src", "DotaTune", "App.axaml"));
        Dictionary<string, string> installer = Brushes(Path.Combine(RepoPaths.Root, "src", "DotaTune.Setup", "App.axaml"));

        foreach ((string key, string colour) in installer)
        {
            Assert.True(program.TryGetValue(key, out string? expected), $"{key} is not in the program palette");
            Assert.Equal(expected, colour);
        }

        string keys = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "DotaTune.Setup", "App.axaml"));
        foreach (string file in new[] { Path.Combine("DotaTune", "Styles", "Theme.axaml"), Path.Combine("DotaTune.Setup", "SetupWindow.axaml") })
        {
            foreach (Match used in StaticResource().Matches(File.ReadAllText(Path.Combine(RepoPaths.Root, "src", file))))
            {
                Assert.Contains($"x:Key=\"{used.Groups[1].Value}\"", keys, StringComparison.Ordinal);
            }
        }
    }

    private static Dictionary<string, string> Brushes(string file)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(file).Descendants()
            .Where(e => e.Name.LocalName == "SolidColorBrush" && e.Attribute(x + "Key") is not null)
            .ToDictionary(e => (string)e.Attribute(x + "Key")!, e => (string)e.Attribute("Color")!, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\{StaticResource (\w+)\}")]
    private static partial Regex StaticResource();

    private static SetupViewModel Model(SetupCase setupCase, Version? installed, FakeSteps steps, CyrillicTempFolder folder) =>
        new(setupCase, new Version(0, 2, 0), installed, steps, new FileLog(Path.Combine(folder.Path, "logs")));

    private static void Settle(SetupViewModel model)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        do
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        while (model.IsBusy && DateTime.UtcNow < until);

        Dispatcher.UIThread.RunJobs();
        Assert.False(model.IsBusy, "the installer did not settle in 10 s");
    }

    private static void Snapshot(Window window, string name)
    {
        for (int i = 0; i < 4; i++)
        {
            Thread.Sleep(100);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        string dir = Path.Combine(RepoPaths.Root, "artifacts", "snapshots");
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, name), new PngBitmapEncoderOptions());
    }

    /// <summary>Records what the installer asked for instead of touching the machine.</summary>
    private sealed class FakeSteps : ISetupSteps
    {
        public List<string> Calls { get; } = [];

        public bool HasPayload { get; init; } = true;

        public bool Running { get; set; }

        public bool Opens { get; init; } = true;

        public Task<int> Install { get; init; } = Task.FromResult(0);

        public bool AppRunning() => Running;

        public Task<bool> CloseAppAsync()
        {
            Calls.Add("close");
            Running = false;
            return Task.FromResult(true);
        }

        public Task<int> InstallAsync()
        {
            Calls.Add("install");
            return Install;
        }

        public bool OpenApp()
        {
            Calls.Add("open");
            return Opens;
        }

        public void OpenLogs() => Calls.Add("logs");
    }
}
