using DotaTune.Core.Diagnostics;
using DotaTune.Core.Settings;

namespace DotaTune.Tests;

public class StorageTests
{
    [Theory]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    public void Log_lines_are_the_same_under_any_culture(string culture)
    {
        using var folder = new CyrillicTempFolder();
        using var scope = new CultureScope(culture);
        var moment = new DateTimeOffset(2026, 10, 26, 9, 5, 7, 123, TimeSpan.FromHours(3));
        var log = new FileLog(Path.Combine(folder.Path, "журнал"), () => moment);

        log.Info("Путь C:\\Users\\Иван");

        string text = File.ReadAllText(log.CurrentFile);
        Assert.Equal("2026-10-26T09:05:07.123+03:00 INFO    Путь C:\\Users\\Иван" + Environment.NewLine, text);
        Assert.EndsWith("dotatune-2026-10-26.log", log.CurrentFile, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_files_are_utf8_without_bom()
    {
        using var folder = new CyrillicTempFolder();
        var log = new FileLog(folder.Path);

        log.Warning("кириллица");

        byte[] bytes = File.ReadAllBytes(log.CurrentFile);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("кириллица", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Old_log_files_are_removed()
    {
        using var folder = new CyrillicTempFolder();
        string old = Path.Combine(folder.Path, "dotatune-2026-09-01.log");
        string recent = Path.Combine(folder.Path, "dotatune-2026-10-20.log");
        File.WriteAllText(old, "x");
        File.WriteAllText(recent, "x");
        var log = new FileLog(folder.Path, () => new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero));

        log.Info("new day");

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    public void Settings_round_trip_in_a_cyrillic_path(string culture)
    {
        using var folder = new CyrillicTempFolder();
        using var scope = new CultureScope(culture);
        var store = new SettingsStore(Path.Combine(folder.Path, "Иван", "settings.json"), new FileLog(folder.Path));

        store.Save(new AppSettings { Language = "ru" });

        Assert.Equal("ru", store.Load().Language);
    }

    [Fact]
    public void Broken_settings_give_defaults_and_stay_on_disk()
    {
        using var folder = new CyrillicTempFolder();
        string file = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(file, "{ not json");
        var log = new FileLog(Path.Combine(folder.Path, "logs"));

        AppSettings settings = new SettingsStore(file, log).Load();

        Assert.Null(settings.Language);
        Assert.Equal("{ not json", File.ReadAllText(file));
        Assert.Contains("unreadable", File.ReadAllText(log.CurrentFile), StringComparison.Ordinal);
    }
}
