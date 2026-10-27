using System.Text;
using System.Text.Json.Nodes;
using DotaTune.Core.Config;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Steam;

namespace DotaTune.Tests;

public class TransferTests
{
    private const uint From = FakeSteam.OtherAccountId;
    private const uint Into = FakeSteam.AccountId;

    // "х" is D1 85 in UTF-8: the 0x85 byte must not be taken for a line end.
    private const string Settings = "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"dota_chat_name\"\t\t\"хорошо\"\n\t}\n}\n";

    [Fact]
    public void Newest_settings_copy_goes_into_all_three_with_the_games_line_ends()
    {
        using var steam = new FakeSteam();
        string old = steam.WriteGameFile(From, "remote/user_convars.vcfg", "\"config\"\n{\n}\n");
        steam.WriteGameFile(From, "local/cfg/user_convars_0_slot0.vcfg", Settings.Replace("\n", "\r\n", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));

        IReadOnlyList<(string Path, byte[] Content)> files = TransferFiles.Plan(steam.AccountGameFolder(From), steam.AccountGameFolder(Into), [TransferGroup.GameSettings]);

        Assert.Equal(
            ["remote/user_convars.vcfg", "local/cfg/user_convars_0_slot0.vcfg", "local/cfg/user_convars_0_slot0.vcfg_lastclouded"],
            files.Select(f => f.Path));
        Assert.Equal(Settings, Encoding.UTF8.GetString(files[0].Content));
        Assert.Equal(Settings.Replace("\n", "\r\n", StringComparison.Ordinal), Encoding.UTF8.GetString(files[1].Content));
        Assert.Equal(files[1].Content, files[2].Content);
    }

    [Fact]
    public void Hero_grids_are_added_with_the_mark_and_replaced_on_the_next_transfer()
    {
        byte[] source = Encoding.UTF8.GetBytes("""{"version":3,"configs":[{"config_name":"Мои","categories":[{"category_name":"Керри","hero_ids":[1]}]}]}""");
        byte[] target = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("""{"version":3,"configs":[{"config_name":"Свои","categories":[]},{"config_name":"Старые #DotaTune","categories":[]}],"extra":1}""")];

        JsonObject merged = JsonNode.Parse(TransferFiles.MergeHeroGrids(source, target))!.AsObject();

        Assert.Equal(["Свои", "Мои #DotaTune"], merged["configs"]!.AsArray().Select(c => (string)c!["config_name"]!));
        Assert.Equal(1, (int)merged["extra"]!);
        Assert.Equal(1, (int)merged["configs"]![1]!["categories"]![0]!["hero_ids"]![0]!);
        JsonObject again = JsonNode.Parse(TransferFiles.MergeHeroGrids(TransferFiles.MergeHeroGrids(source, null), null))!.AsObject();
        Assert.Equal(["Мои #DotaTune"], again["configs"]!.AsArray().Select(c => (string)c!["config_name"]!));
        Assert.Throws<InvalidDataException>(() => TransferFiles.MergeHeroGrids(source, Encoding.UTF8.GetBytes("{\"version\":3")));
        Assert.Throws<InvalidDataException>(() => TransferFiles.MergeHeroGrids(Encoding.UTF8.GetBytes("[]"), null));
    }

    [Fact]
    public void Transfer_copies_the_groups_keeps_personal_files_and_puts_back()
    {
        using var steam = new FakeSteam();
        steam.WriteGameFile(From, "remote/cfg/dotakeys_personal.lst", "keys from");
        steam.WriteGameFile(From, "remote/cfg/stats.dat", "personal");
        steam.WriteGameFile(From, "remote/guides/axe.build", "guide");
        steam.WriteGameFile(From, "local/cfg/video.txt", "video from");
        string keys = steam.WriteGameFile(Into, "remote/cfg/dotakeys_personal.lst", "keys into");
        string video = steam.WriteGameFile(Into, "local/cfg/video.txt", "video into");
        TransferService service = Service(steam);

        TransferResult result = service.Apply(steam.Read(), From, Into, [TransferGroup.Hotkeys, TransferGroup.Builds]);

        Assert.Equal(new TransferResult(null, 2), result);
        Assert.Equal("keys from", File.ReadAllText(keys));
        Assert.Equal("guide", File.ReadAllText(Path.Combine(steam.AccountGameFolder(Into), "remote", "guides", "axe.build")));
        Assert.False(File.Exists(Path.Combine(steam.AccountGameFolder(Into), "remote", "cfg", "stats.dat")));
        Assert.Equal("video into", File.ReadAllText(video));

        // A second transfer keeps the first copy of the hotkeys.
        steam.WriteGameFile(From, "remote/cfg/dotakeys_personal.lst", "keys from again");
        Assert.Null(service.Apply(steam.Read(), From, Into, [TransferGroup.Hotkeys, TransferGroup.Graphics]).Refusal);
        Assert.Equal("video from", File.ReadAllText(video));

        Assert.Equal(new TransferResult(null, 3), service.Restore(steam.Read(), Into));

        Assert.Equal("keys into", File.ReadAllText(keys));
        Assert.Equal("video into", File.ReadAllText(video));
        Assert.False(File.Exists(Path.Combine(steam.AccountGameFolder(Into), "remote", "guides", "axe.build")));
        Assert.Null(service.BackupOf(Into));
        Assert.False(Directory.Exists(Path.Combine(steam.DataRoot, "transfer", Into.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void Put_back_leaves_what_changed_after_the_transfer()
    {
        using var steam = new FakeSteam();
        steam.WriteGameFile(From, "remote/cfg/dotakeys_personal.lst", "keys from");
        steam.WriteGameFile(From, "local/cfg/video.txt", "video from");
        steam.WriteGameFile(From, "remote/guides/axe.build", "axe guide");
        steam.WriteGameFile(From, "remote/guides/lina.build", "lina guide");
        string keys = steam.WriteGameFile(Into, "remote/cfg/dotakeys_personal.lst", "keys into");
        string video = steam.WriteGameFile(Into, "local/cfg/video.txt", "video into");
        string axe = Path.Combine(steam.AccountGameFolder(Into), "remote", "guides", "axe.build");
        string lina = Path.Combine(steam.AccountGameFolder(Into), "remote", "guides", "lina.build");
        steam.SetLaunchOptions("-novid", From);
        steam.SetLaunchOptions("-console");
        TransferService service = Service(steam);
        TransferGroup[] groups = [TransferGroup.Hotkeys, TransferGroup.Graphics, TransferGroup.Builds, TransferGroup.LaunchOptions];
        Assert.Null(service.Apply(steam.Read(), From, Into, groups).Refusal);

        // A week of playing: other hotkeys, an edited guide, another launch line.
        File.WriteAllText(keys, "keys of the player");
        File.WriteAllText(axe, "axe guide edited");
        steam.SetLaunchOptions("-novid -high");

        Assert.Equal(new TransferResult(TransferRefusal.ChangedSince, 2), service.Restore(steam.Read(), Into));

        Assert.Equal("keys of the player", File.ReadAllText(keys));
        Assert.Equal("axe guide edited", File.ReadAllText(axe));
        Assert.Equal("-novid -high", LaunchOptions.Read(steam.SteamRoot, Into));
        Assert.Equal("video into", File.ReadAllText(video));
        Assert.False(File.Exists(lina));
        Assert.Null(service.BackupOf(Into));
        Assert.False(Directory.Exists(Path.Combine(steam.DataRoot, "transfer", Into.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void Put_back_passes_over_what_is_already_as_before()
    {
        using var steam = new FakeSteam();
        steam.WriteGameFile(From, "remote/cfg/dotakeys_personal.lst", "keys from");
        steam.WriteGameFile(From, "local/cfg/video.txt", "video from");
        string keys = steam.WriteGameFile(Into, "remote/cfg/dotakeys_personal.lst", "keys into");
        string video = steam.WriteGameFile(Into, "local/cfg/video.txt", "video into");
        steam.SetLaunchOptions("-novid", From);
        steam.SetLaunchOptions("-console");
        TransferService service = Service(steam);
        Assert.Null(service.Apply(steam.Read(), From, Into, [TransferGroup.Hotkeys, TransferGroup.Graphics, TransferGroup.LaunchOptions]).Refusal);

        // The player set the hotkeys and the line back by hand.
        File.WriteAllText(keys, "keys into");
        steam.SetLaunchOptions("-console");

        Assert.Equal(new TransferResult(null, 1), service.Restore(steam.Read(), Into));
        Assert.Equal("keys into", File.ReadAllText(keys));
        Assert.Equal("video into", File.ReadAllText(video));
        Assert.Equal("-console", LaunchOptions.Read(steam.SteamRoot, Into));
    }

    [Fact]
    public void Line_the_transfer_could_not_write_is_not_reported_as_changed()
    {
        // The game was never started on the account: localconfig.vdf has no Dota block to write into.
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-novid", From);
        Directory.CreateDirectory(steam.AccountGameFolder(From));
        Directory.CreateDirectory(steam.AccountGameFolder(Into));
        string config = LaunchOptions.LocalConfigPath(steam.SteamRoot, Into);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"Apps\"\n\t\t\t\t{\n\t\t\t\t\t\"7\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-not-dota\"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n");
        TransferService service = Service(steam);
        Assert.Equal(new TransferResult(null, 0), service.Apply(steam.Read(), From, Into, [TransferGroup.LaunchOptions]));

        Assert.Equal(new TransferResult(null, 0), service.Restore(steam.Read(), Into));
        Assert.Null(service.BackupOf(Into));
    }

    [Fact]
    public void Records_of_earlier_builds_are_put_back_unchecked()
    {
        // transfer.json as builds before the check wrote it: no "written", no "launchWritten".
        using var steam = new FakeSteam();
        string keys = steam.WriteGameFile(Into, "remote/cfg/dotakeys_personal.lst", "keys of the player");
        string folder = Path.Combine(steam.DataRoot, "transfer", Into.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "0.bak"), "keys before");
        File.WriteAllText(Path.Combine(steam.DataRoot, "transfer.json"), $$"""
            [{"accountId":{{Into}},"files":[{"relativePath":"remote/cfg/dotakeys_personal.lst","backup":"0.bak"}],
              "launchTouched":false,"launchBefore":null,"at":"2026-10-26T20:00:00+03:00"}]
            """);

        Assert.Equal(new TransferResult(null, 1), Service(steam).Restore(steam.Read(), Into));
        Assert.Equal("keys before", File.ReadAllText(keys));
    }

    [Fact]
    public void Launch_options_move_only_while_steam_is_closed()
    {
        using var steam = new FakeSteam();
        steam.WriteGameFile(From, "remote/cfg/chat.cfg", "chat");
        steam.WriteGameFile(Into, "remote/cfg/chat.cfg", "old chat");
        steam.SetLaunchOptions("-novid -high", From);
        steam.SetLaunchOptions("-console");
        TransferService service = Service(steam);

        steam.Running.Add("steam");
        Assert.Equal(TransferRefusal.SteamRunning, service.Apply(steam.Read(), From, Into, [TransferGroup.LaunchOptions, TransferGroup.Misc]).Refusal);
        Assert.Null(service.BackupOf(Into));
        steam.Running.Clear();

        Assert.Equal(new TransferResult(null, 2), service.Apply(steam.Read(), From, Into, [TransferGroup.LaunchOptions, TransferGroup.Misc]));
        Assert.Equal("-novid -high", LaunchOptions.Read(steam.SteamRoot, Into));

        steam.Running.Add("steam");
        Assert.Equal(TransferRefusal.SteamRunning, service.Restore(steam.Read(), Into).Refusal);
        steam.Running.Clear();
        Assert.Null(service.Restore(steam.Read(), Into).Refusal);
        Assert.Equal("-console", LaunchOptions.Read(steam.SteamRoot, Into));
    }

    [Fact]
    public void Hero_grids_go_only_into_an_account_whose_game_reads_them()
    {
        using var steam = new FakeSteam();
        steam.WriteGameFile(From, TransferFiles.HeroGridPath, """{"version":3,"configs":[{"config_name":"Мои","categories":[]}]}""");
        steam.WriteGameFile(From, "remote/cfg/chat.cfg", "chat");
        Directory.CreateDirectory(steam.AccountGameFolder(Into));
        string grids = Path.Combine(steam.AccountGameFolder(Into), "remote", "cfg", "hero_grid_config.json");
        TransferService service = Service(steam);

        Assert.Equal(new TransferResult(TransferRefusal.GridsNotSeen, 0, GridsSkipped: true), service.Apply(steam.Read(), From, Into, [TransferGroup.HeroGrids]));
        Assert.Null(service.BackupOf(Into));
        Assert.Equal(new TransferResult(null, 1, GridsSkipped: true), service.Apply(steam.Read(), From, Into, [TransferGroup.HeroGrids, TransferGroup.Misc]));
        Assert.False(File.Exists(grids));
        Assert.DoesNotContain(service.BackupOf(Into)!.Files, f => f.RelativePath == TransferFiles.HeroGridPath);

        steam.SetGridSeen(Into);
        Assert.Equal(new TransferResult(null, 1), service.Apply(steam.Read(), From, Into, [TransferGroup.HeroGrids]));
        Assert.Contains("Мои #DotaTune", File.ReadAllText(grids), StringComparison.Ordinal);
    }

    [Fact]
    public void Preset_hero_grids_go_only_into_an_account_whose_game_reads_them()
    {
        using var steam = new FakeSteam();
        Directory.CreateDirectory(steam.AccountGameFolder(Into));
        byte[] grids = Encoding.UTF8.GetBytes("""{"version":3,"configs":[]}""");
        byte[] chat = Encoding.UTF8.GetBytes("chat");
        TransferService service = Service(steam);

        Assert.Equal(new TransferResult(TransferRefusal.GridsNotSeen, 0, GridsSkipped: true), service.ApplyFiles(steam.Read(), Into, [(TransferFiles.HeroGridPath, grids)]));
        Assert.Equal(new TransferResult(null, 1, GridsSkipped: true), service.ApplyFiles(steam.Read(), Into, [(TransferFiles.HeroGridPath, grids), ("remote/cfg/chat.cfg", chat)]));
        Assert.False(File.Exists(Path.Combine(steam.AccountGameFolder(Into), "remote", "cfg", "hero_grid_config.json")));

        steam.SetGridSeen(Into);
        Assert.Equal(new TransferResult(null, 1), service.ApplyFiles(steam.Read(), Into, [(TransferFiles.HeroGridPath, grids)]));
        Assert.True(File.Exists(Path.Combine(steam.AccountGameFolder(Into), "remote", "cfg", "hero_grid_config.json")));
        Assert.Throws<ArgumentException>(() => TransferFiles.SteamLists(steam.AccountGameFolder(Into), "local/cfg/video.txt"));
    }

    [Fact]
    public void Transfer_is_refused_when_it_cannot_or_need_not_run()
    {
        using var steam = new FakeSteam();
        steam.WriteGameFile(From, "remote/cfg/chat.cfg", "chat");
        steam.WriteGameFile(Into, "remote/cfg/chat.cfg", "chat");
        TransferService service = Service(steam);

        Assert.Equal(TransferRefusal.SameAccount, service.Apply(steam.Read(), Into, Into, [TransferGroup.Misc]).Refusal);
        Assert.Equal(TransferRefusal.NoSuchAccount, service.Apply(steam.Read(), 42, Into, [TransferGroup.Misc]).Refusal);
        Assert.Equal(TransferRefusal.NothingToCopy, service.Apply(steam.Read(), From, Into, [TransferGroup.Hotkeys]).Refusal);
        Assert.Equal(TransferRefusal.NothingToCopy, service.Restore(steam.Read(), Into).Refusal);
        steam.Running.Add("dota2");
        Assert.Equal(TransferRefusal.DotaRunning, service.Apply(steam.Read(), From, Into, [TransferGroup.Misc]).Refusal);
        Assert.Null(service.BackupOf(Into));
    }

    [Fact]
    public void Accounts_have_names_and_the_cloud_switch()
    {
        using var steam = new FakeSteam();
        steam.WriteGameFile(From, "remote/cfg/chat.cfg", "chat");
        steam.WriteGameFile(Into, "remote/cfg/chat.cfg", "chat");
        steam.WriteGameFile(0, "remote/cfg/chat.cfg", "chat");
        steam.SetCloud(Into, on: false);

        IReadOnlyList<GameAccount> accounts = TransferService.Accounts(steam.Read());

        Assert.Equal(
            [new GameAccount(From, "другой", CloudEnabled: true), new GameAccount(Into, "demo_player", CloudEnabled: false)],
            accounts);
        steam.SetCloud(Into, on: true);
        Assert.True(TransferService.CloudEnabled(steam.SteamRoot, Into));
    }

    private static TransferService Service(FakeSteam steam)
    {
        var log = new FileLog(Path.Combine(steam.DataRoot, "logs"));
        return new TransferService(new TransferBackupStore(Path.Combine(steam.DataRoot, "transfer.json"), Path.Combine(steam.DataRoot, "transfer"), log), log);
    }
}
