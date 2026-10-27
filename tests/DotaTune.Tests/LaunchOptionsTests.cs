using System.Text;
using DotaTune.Core.Config;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Steam;

namespace DotaTune.Tests;

public class LaunchOptionsTests
{
    [Fact]
    public void Parse_keeps_values_with_their_option()
    {
        IReadOnlyList<LaunchOption> options = LaunchOptionsEditor.Parse("-map dota +fps_max 0 -novid -language \"русский язык\" -x -1");

        Assert.Equal(
            ["-map dota", "+fps_max 0", "-novid", "-language русский язык", "-x -1"],
            options.Select(o => o.ToString()));
    }

    [Fact]
    public void Plan_takes_out_dead_options_and_keeps_the_rest_in_place()
    {
        Assert.Equal("-high +fps_max 0 -novid", LaunchOptionsEditor.Plan("-prewarm -high -map dota +fps_max 0", noVideo: true, noJoystick: false, removeDead: true));
        Assert.Equal("-prewarm -high", LaunchOptionsEditor.Plan("-prewarm -high", noVideo: false, noJoystick: false, removeDead: false));
        Assert.Equal("-high", LaunchOptionsEditor.Plan("-NOVID -high -nojoy", noVideo: false, noJoystick: false, removeDead: true));
        Assert.Equal("-novid -nojoy", LaunchOptionsEditor.Plan(null, noVideo: true, noJoystick: true, removeDead: true));
        Assert.Equal("-novid", LaunchOptionsEditor.Plan("-novid", noVideo: true, noJoystick: false, removeDead: true));

        // Nothing to change: spaces and quotes stay as the player typed them.
        Assert.Equal("-console  -language \"russian\" -novid", LaunchOptionsEditor.Plan("-console  -language \"russian\" -novid", noVideo: true, noJoystick: false, removeDead: true));
        Assert.Equal(string.Empty, LaunchOptionsEditor.Plan(null, noVideo: false, noJoystick: false, removeDead: true));
    }

    [Fact]
    public void Join_quotes_values_with_spaces_and_empty_ones()
    {
        string line = LaunchOptionsEditor.Join(LaunchOptionsEditor.Parse("-language \"русский язык\"   -high"));

        Assert.Equal("-language \"русский язык\" -high", line);
        Assert.Equal("-name \"\"", LaunchOptionsEditor.Join([new LaunchOption("-name", [string.Empty])]));
    }

    [Fact]
    public void Local_config_value_is_replaced_and_every_other_byte_stays()
    {
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-prewarm");
        string file = LaunchOptions.LocalConfigPath(steam.SteamRoot, FakeSteam.AccountId);
        byte[] before = File.ReadAllBytes(file);

        Assert.True(LocalConfigEditor.SetLaunchOptions(file, "-novid -language \"русский\" -path C:\\Игры"));

        Assert.Equal("-novid -language \"русский\" -path C:\\Игры", LaunchOptions.Read(steam.SteamRoot, FakeSteam.AccountId));
        string expected = Encoding.UTF8.GetString(before).Replace(
            "\"LaunchOptions\"\t\t\"-prewarm\"",
            "\"LaunchOptions\"\t\t\"-novid -language \\\"русский\\\" -path C:\\\\Игры\"",
            StringComparison.Ordinal);
        Assert.Equal(expected, Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        Assert.Contains("\"LaunchOptions\"\t\t\"-not-dota\"", expected, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Local_config_without_the_key_gets_a_line_in_the_dota_block(string newline)
    {
        using var steam = new FakeSteam();
        string file = LaunchOptions.LocalConfigPath(steam.SteamRoot, FakeSteam.AccountId);
        string text = File.ReadAllText(file).Replace("\t\t\t\t\t\t\"LaunchOptions\"\t\t\"\"\n", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("\"\"", text, StringComparison.Ordinal);
        File.WriteAllText(file, text.ReplaceLineEndings(newline));

        Assert.True(LocalConfigEditor.SetLaunchOptions(file, "-novid"));

        Assert.Equal("-novid", LaunchOptions.Read(steam.SteamRoot, FakeSteam.AccountId));
        Assert.Contains(
            $"\"LastPlayed\"\t\t\"1793001750\"{newline}\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-novid\"{newline}\t\t\t\t\t}}",
            File.ReadAllText(file),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Local_config_without_a_dota_block_is_left_alone()
    {
        using var steam = new FakeSteam();
        string file = LaunchOptions.LocalConfigPath(steam.SteamRoot, FakeSteam.AccountId);
        steam.Write(file, "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t}\n}\n");
        byte[] before = File.ReadAllBytes(file);

        Assert.False(LocalConfigEditor.SetLaunchOptions(file, "-novid"));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Throws<ArgumentException>(() => LocalConfigEditor.SetLaunchOptions(file, "-novid\n-high"));
        steam.Write(file, "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n");
        Assert.Throws<InvalidDataException>(() => LocalConfigEditor.SetLaunchOptions(file, "-novid"));
    }

    [Fact]
    public void Service_refuses_while_steam_runs_and_puts_the_first_line_back()
    {
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-prewarm -high");
        var service = Service(steam);

        steam.Running.Add("steam");
        Assert.Equal(LaunchRefusal.SteamRunning, service.Apply(steam.Read(), "-high"));
        Assert.Equal("-prewarm -high", LaunchOptions.Read(steam.SteamRoot, FakeSteam.AccountId));
        steam.Running.Clear();

        Assert.Null(service.Apply(steam.Read(), "-high"));
        Assert.Null(service.Apply(steam.Read(), "-high -novid"));
        LaunchChange change = service.ChangeOf(FakeSteam.AccountId)!;
        Assert.Equal("-prewarm -high", change.Before);
        Assert.Equal("-high -novid", change.Written);

        steam.Running.Add("steam");
        Assert.Equal(LaunchRefusal.SteamRunning, service.Restore(steam.Read()));
        steam.Running.Clear();
        Assert.Null(service.Restore(steam.Read()));
        Assert.Equal("-prewarm -high", LaunchOptions.Read(steam.SteamRoot, FakeSteam.AccountId));
        Assert.Null(service.ChangeOf(FakeSteam.AccountId));
    }

    [Fact]
    public void Writing_the_first_line_again_forgets_the_change()
    {
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-high");
        var service = Service(steam);

        Assert.Null(service.Apply(steam.Read(), "-high -novid"));
        Assert.Null(service.Apply(steam.Read(), "-high"));

        Assert.Null(service.ChangeOf(FakeSteam.AccountId));
    }

    [Fact]
    public void Restore_leaves_a_line_changed_in_steam_since()
    {
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-prewarm");
        var service = Service(steam);
        Assert.Null(service.Apply(steam.Read(), "-novid"));
        steam.SetLaunchOptions("-novid -console");

        Assert.Equal(LaunchRefusal.ChangedInSteam, service.RestoreAll(steam.Read()));

        Assert.Equal("-novid -console", LaunchOptions.Read(steam.SteamRoot, FakeSteam.AccountId));
        Assert.Null(service.ChangeOf(FakeSteam.AccountId));
    }

    [Fact]
    public void Line_changed_in_steam_since_becomes_the_line_to_put_back()
    {
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-prewarm");
        var service = Service(steam);
        Assert.Null(service.Apply(steam.Read(), "-novid"));
        steam.SetLaunchOptions("-novid -console");

        Assert.Null(service.Apply(steam.Read(), "-novid -console -nojoy"));
        Assert.Equal("-novid -console", service.ChangeOf(FakeSteam.AccountId)!.Before);
        Assert.Null(service.Restore(steam.Read()));

        Assert.Equal("-novid -console", LaunchOptions.Read(steam.SteamRoot, FakeSteam.AccountId));
    }

    [Fact]
    public void Restore_all_works_without_finding_steam_again()
    {
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-prewarm");
        var service = Service(steam);
        Assert.Null(service.Apply(steam.Read(), "-novid"));

        Assert.Null(service.RestoreAll(steam.Read() with { Steam = null }));

        Assert.Equal("-prewarm", LaunchOptions.Read(steam.SteamRoot, FakeSteam.AccountId));
        Assert.Null(service.ChangeOf(FakeSteam.AccountId));
    }

    [Fact]
    public void Account_without_a_dota_block_is_refused_and_the_record_stays_as_it_was()
    {
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-prewarm");
        var service = Service(steam);
        Assert.Null(service.Apply(steam.Read(), "-novid"));
        LaunchChange before = service.ChangeOf(FakeSteam.AccountId)!;
        steam.Write(LaunchOptions.LocalConfigPath(steam.SteamRoot, FakeSteam.AccountId), "\"UserLocalConfigStore\"\n{\n}\n");

        Assert.Equal(LaunchRefusal.NoDotaBlock, service.Apply(steam.Read(), "-nojoy"));
        Assert.Equal(before, service.ChangeOf(FakeSteam.AccountId));
    }

    private static LaunchService Service(FakeSteam steam)
    {
        var log = new FileLog(Path.Combine(steam.DataRoot, "logs"));
        return new LaunchService(new LaunchChangeStore(Path.Combine(steam.DataRoot, "launch.json"), log), log);
    }
}
