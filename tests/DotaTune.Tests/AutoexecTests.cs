using System.Text;
using System.Text.Json;
using DotaTune.Core.Config;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Settings;
using DotaTune.Core.Text;

namespace DotaTune.Tests;

public class AutoexecTests
{
    private static readonly ConfigLine[] Colors =
    [
        new("dota_friendly_color", "0 255 255"),
        new("dota_enemy_color", "255 0 255"),
    ];

    [Fact]
    public void Block_goes_after_own_lines_and_leaves_their_bytes_alone()
    {
        // A comment in UTF-8 Cyrillic and Windows line breaks, as a person writes it in Notepad.
        byte[] own = Encoding.UTF8.GetBytes("// мой конфиг\r\nbind \"f11\" \"disconnect\"\r\n");
        string text = AutoexecText.Encoding.GetString(own);

        string with = AutoexecText.WithBlock(text, Colors);

        Assert.StartsWith(text, with, StringComparison.Ordinal);
        Assert.EndsWith($"{AutoexecText.BeginMarker}\r\ndota_friendly_color 0 255 255\r\ndota_enemy_color 255 0 255\r\n{AutoexecText.EndMarker}\r\n", with, StringComparison.Ordinal);
        Assert.Equal(Colors, AutoexecText.BlockLines(with));
        Assert.Equal(own, AutoexecText.Encoding.GetBytes(AutoexecText.WithoutBlock(with, dropBreakBefore: false)));
    }

    [Fact]
    public void Block_is_replaced_in_place_and_own_lines_after_it_stay()
    {
        string text = AutoexecText.WithBlock("fps_max 240\n", Colors) + "cl_showfps 1\n";

        string with = AutoexecText.WithBlock(text, [new ConfigLine("dota_neutral_color", "255 255 0")]);

        Assert.Equal($"fps_max 240\n{AutoexecText.BeginMarker}\ndota_neutral_color 255 255 0\n{AutoexecText.EndMarker}\ncl_showfps 1\n", with);
    }

    [Fact]
    public void Line_break_added_before_the_block_goes_away_with_it()
    {
        string with = AutoexecText.WithBlock("fps_max 240", Colors);

        Assert.StartsWith($"fps_max 240\n{AutoexecText.BeginMarker}", with, StringComparison.Ordinal);
        Assert.Equal("fps_max 240", AutoexecText.WithoutBlock(with, dropBreakBefore: true));
    }

    [Theory]
    [InlineData("// DotaTune begin\n")]
    [InlineData("// DotaTune end\n")]
    [InlineData("// DotaTune end\n// DotaTune begin\n")]
    [InlineData("// DotaTune begin\n// DotaTune end\n// DotaTune begin\n// DotaTune end\n")]
    public void Broken_marks_are_not_guessed_at(string text)
    {
        Assert.Throws<InvalidDataException>(() => AutoexecText.FindBlock(text));
        Assert.Throws<InvalidDataException>(() => AutoexecText.WithBlock(text, Colors));
    }

    [Fact]
    public void Own_comment_about_dotatune_is_not_a_mark()
    {
        string text = "// DotaTune end of my binds\n// DotaTune beginner tips\n";
        Assert.Null(AutoexecText.FindBlock(text));
        Assert.Single(AutoexecText.BlockLines(AutoexecText.WithBlock(text, Colors[..1]))!);
    }

    [Fact]
    public void Bad_line_keeps_the_earlier_block()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        AutoexecService service = Service(folder);
        Assert.Null(service.Apply(steam.Read(), Colors));

        Assert.Throws<ArgumentException>(() => service.Apply(steam.Read(), [new ConfigLine("say", "hi; quit")]));

        Assert.Equal(AutoexecHealth.Ready, service.Check());
    }

    [Fact]
    public void Line_that_would_break_the_console_is_refused()
    {
        Assert.Throws<ArgumentException>(() => AutoexecText.WithBlock(string.Empty, [new ConfigLine("say", "hi; quit")]));
        Assert.Throws<ArgumentException>(() => AutoexecText.WithBlock(string.Empty, [new ConfigLine("bind", "\"f1\" \"quit\"")]));
    }

    [Fact]
    public void Choice_gives_lines_in_game_format_under_any_culture()
    {
        using var culture = new CultureScope("ru-RU");
        var config = new GameConfig
        {
            FriendlyColor = new Rgb(0, 255, 255),
            NeutralColor = new Rgb(255, 255, 0),
            CreepScale = 1.75,
            MenuFpsLimit = 120,
            HideFrontpageVideo = true,
            NoParticleLights = true,
        };

        Assert.Equal(
            ["dota_friendly_color 0 255 255", "dota_neutral_color 255 255 0", "dota_minimap_creep_scale 1.75", "dota_hide_frontpage_video 1", "fps_max_ui 120", "dota_disable_particle_lights 1"],
            config.Lines().Select(l => l.ToString()));
        Assert.Empty(new GameConfig { CreepScale = 1, MenuFpsLimit = 5 }.Lines());
    }

    [Fact]
    public void Minimalism_goes_in_after_the_own_lines_and_leaves_the_minimap_colours_alone()
    {
        IReadOnlyList<ConfigLine> alone = new GameConfig { Minimalism = true }.Lines();
        Assert.Equal(MinimalismPreset.Lines, alone);
        Assert.Contains(new ConfigLine("r_dota_color_correction", "0"), alone);
        Assert.DoesNotContain(alone, l => GameConfig.ColorNames.Contains(l.Name));
        Assert.Equal(alone.Count, alone.Select(l => l.Name).Distinct().Count());

        // Its particle light line is also a switch: one line either way, and the own colour stays.
        IReadOnlyList<ConfigLine> both = new GameConfig { Minimalism = true, NoParticleLights = true, EnemyColor = new Rgb(1, 2, 3) }.Lines();
        Assert.Equal("dota_enemy_color 1 2 3", both[0].ToString());
        Assert.Single(both, l => l.Name == "dota_disable_particle_lights");
        Assert.Equal(alone.Count + 1, both.Count);

        // Every line is a plain console line the block takes.
        Assert.Equal(alone, AutoexecText.BlockLines(AutoexecText.WithBlock(string.Empty, alone)));
    }

    [Fact]
    public void Colours_are_kept_as_codes_and_a_bad_one_spoils_nothing_else()
    {
        using var folder = new CyrillicTempFolder();
        var store = new SettingsStore(Path.Combine(folder.Path, "settings.json"), new FileLog(Path.Combine(folder.Path, "logs")));
        store.Save(new AppSettings { Language = "ru", Config = new GameConfig { EnemyColor = new Rgb(255, 0, 255), CreepScale = 1.5 } });

        Assert.Contains("\"#FF00FF\"", File.ReadAllText(Path.Combine(folder.Path, "settings.json")), StringComparison.Ordinal);
        Assert.Equal(new Rgb(255, 0, 255), store.Load().Config!.EnemyColor);

        File.WriteAllText(Path.Combine(folder.Path, "settings.json"),
            """{ "language": "ru", "config": { "enemyColor": "розовый", "friendlyColor": 7, "creepScale": 1.5 } }""");
        AppSettings loaded = store.Load();
        Assert.Equal("ru", loaded.Language);
        Assert.Null(loaded.Config!.EnemyColor);
        Assert.Null(loaded.Config.FriendlyColor);
        Assert.Equal(1.5, loaded.Config.CreepScale);
    }

    [Theory]
    [InlineData("true", "1", true)]
    [InlineData("false", "0", true)]
    [InlineData("120.000000", "120", true)]
    [InlineData("600", "800", false)]
    [InlineData("0 255 255", "0 255 255", true)]
    public void Saved_value_is_compared_the_way_the_game_prints_it(string saved, string written, bool same) =>
        Assert.Equal(same, ConvarFiles.SameValue(saved, written));

    [Fact]
    public void Convar_line_is_changed_or_taken_out_with_its_line_break()
    {
        using var folder = new CyrillicTempFolder();
        string file = Path.Combine(folder.Path, "machine_convars.vcfg");
        File.WriteAllText(file, "\"config\"\r\n{\r\n\t\"convars\"\r\n\t{\r\n\t\t\"fps_max\"\t\t\"120\"\r\n\t\t\"fps_max_ui\"\t\t\"0\"\r\n\t}\r\n}\r\n");

        Assert.Equal("0", ConvarFiles.Read(file, "FPS_MAX_UI"));
        Assert.True(ConvarFiles.Write(file, "fps_max_ui", "60"));
        Assert.Equal("60", ConvarFiles.Read(file, "fps_max_ui"));
        Assert.True(ConvarFiles.Write(file, "fps_max", null));
        Assert.False(ConvarFiles.Write(file, "fps_max", "1"));
        Assert.Equal("\"config\"\r\n{\r\n\t\"convars\"\r\n\t{\r\n\t\t\"fps_max_ui\"\t\t\"60\"\r\n\t}\r\n}\r\n", File.ReadAllText(file));
    }

    [Fact]
    public void Written_lines_come_out_and_saved_values_return_for_every_account()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        const uint other = 111111111;
        string machine = steam.WriteConvars(Path.Combine("local", "cfg", "machine_convars.vcfg"), [("fps_max_ui", "0"), ("dota_hide_frontpage_video", "false")]);
        string user = steam.WriteConvars(Path.Combine("remote", "user_convars.vcfg"), [("chat_sound", "false")]);
        string otherMachine = steam.WriteConvars(Path.Combine("local", "cfg", "machine_convars.vcfg"), [("fps_max_ui", "0")], other);
        string machineBefore = File.ReadAllText(machine);
        AutoexecService service = Service(folder);
        ConfigLine[] lines = [.. Colors, new("fps_max_ui", "120"), new("dota_hide_frontpage_video", "1")];

        Assert.Null(service.Apply(steam.Read(), lines));
        string autoexec = AutoexecService.PathIn(steam.GameRoot);
        Assert.Equal(lines, AutoexecText.BlockLines(File.ReadAllText(autoexec)));
        Assert.Equal(AutoexecHealth.Ready, service.Check());

        // The game runs autoexec and saves what has the archive flag; the person then moves
        // the menu limit of the other account back by hand.
        ConvarFiles.Write(machine, "fps_max_ui", "120.000000");
        ConvarFiles.Write(machine, "dota_hide_frontpage_video", "true");
        ConvarFiles.Write(otherMachine, "fps_max_ui", "144");

        Assert.Null(service.Remove(steam.Read()));

        Assert.False(File.Exists(autoexec));
        Assert.Equal(machineBefore, File.ReadAllText(machine));
        Assert.Equal("144", ConvarFiles.Read(otherMachine, "fps_max_ui"));
        Assert.Null(ConvarFiles.Read(user, "dota_friendly_color"));
        Assert.Equal(AutoexecHealth.NotWritten, service.Check());
    }

    [Fact]
    public void Own_autoexec_keeps_its_exact_bytes_after_write_and_remove()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        string autoexec = AutoexecService.PathIn(steam.GameRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(autoexec)!);
        // Windows-1251 comment and no line break at the end.
        CodePages.Register();
        byte[] own = [.. Encoding.GetEncoding(1251).GetBytes("// мой\r\nfps_max 240")];
        File.WriteAllBytes(autoexec, own);
        AutoexecService service = Service(folder);

        Assert.Null(service.Apply(steam.Read(), Colors));
        Assert.Null(service.Apply(steam.Read(), [new ConfigLine("dota_neutral_color", "255 255 0")]));
        Assert.Single(AutoexecText.BlockLines(AutoexecText.Encoding.GetString(File.ReadAllBytes(autoexec)))!);
        Assert.Null(service.Remove(steam.Read()));

        Assert.Equal(own, File.ReadAllBytes(autoexec));
    }

    [Fact]
    public void Hand_edits_are_seen_and_nothing_is_written_while_dota_runs()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        AutoexecService service = Service(folder);
        string autoexec = AutoexecService.PathIn(steam.GameRoot);
        Assert.Null(service.Apply(steam.Read(), Colors));

        File.WriteAllText(autoexec, File.ReadAllText(autoexec).Replace("255 0 255", "255 0 0", StringComparison.Ordinal));
        Assert.Equal(AutoexecHealth.Changed, service.Check());
        File.WriteAllText(autoexec, "fps_max 240\n");
        Assert.Equal(AutoexecHealth.Missing, service.Check());

        File.WriteAllText(autoexec, $"{AutoexecText.BeginMarker}\nfps_max 240\n");
        Assert.Equal(ConfigRefusal.BrokenMarks, service.Apply(steam.Read(), Colors));
        Assert.Equal(ConfigRefusal.BrokenMarks, service.Remove(steam.Read()));
        Assert.NotNull(service.Current);

        File.WriteAllText(autoexec, "fps_max 240\n");
        steam.Running.Add("dota2");
        Assert.Equal(ConfigRefusal.DotaRunning, service.Apply(steam.Read(), Colors));
        Assert.Equal(ConfigRefusal.DotaRunning, service.Remove(steam.Read()));
        steam.Running.Clear();

        Assert.Null(service.Apply(steam.Read(), []));
        Assert.Null(service.Current);
        Assert.Equal("fps_max 240\n", File.ReadAllText(autoexec));
    }

    [Fact]
    public void State_records_what_was_replaced()
    {
        using var folder = new CyrillicTempFolder();
        using var steam = new FakeSteam();
        steam.WriteConvars(Path.Combine("local", "cfg", "machine_convars.vcfg"), [("fps_max_ui", "0")]);
        AutoexecService service = Service(folder);

        Assert.Null(service.Apply(steam.Read(), [new ConfigLine("fps_max_ui", "60")]));

        string json = File.ReadAllText(Path.Combine(folder.Path, "autoexec.json"));
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement saved = doc.RootElement.GetProperty("previous")[0];
        Assert.Equal("fps_max_ui", saved.GetProperty("name").GetString());
        Assert.Equal("0", saved.GetProperty("value").GetString());
        Assert.True(doc.RootElement.GetProperty("createdFile").GetBoolean());
    }

    private static AutoexecService Service(CyrillicTempFolder folder)
    {
        var log = new FileLog(Path.Combine(folder.Path, "logs"));
        return new AutoexecService(new AutoexecStateStore(Path.Combine(folder.Path, "autoexec.json"), log), log);
    }
}
