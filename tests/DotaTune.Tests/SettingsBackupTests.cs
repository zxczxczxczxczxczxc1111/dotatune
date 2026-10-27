using System.IO.Compression;
using System.Text;
using DotaTune.Core.Config;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Presets;
using DotaTune.Core.Steam;

namespace DotaTune.Tests;

public class SettingsBackupTests
{
    private const uint Account = FakeSteam.AccountId;

    // "х" is D1 85 in UTF-8: the 0x85 byte must not be taken for a line end.
    private const string Settings = "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"dota_chat_name\"\t\t\"хорошо\"\n\t}\n}\n";

    [Fact]
    public void Copy_holds_the_own_settings_and_reads_back_as_saved()
    {
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-novid -high");
        steam.WriteGameFile(Account, "remote/cfg/dotakeys_personal.lst", "keys");
        steam.WriteGameFile(Account, "remote/user_convars.vcfg", Settings);
        steam.WriteGameFile(Account, "local/cfg/video.txt", "video");
        steam.WriteGameFile(Account, "remote/guides/axe.build", "guide");
        steam.WriteGameFile(Account, "remote/cfg/stats.dat", "personal");
        (SettingsBackupService service, AutoexecService autoexec) = Services(steam);
        steam.Write(AutoexecService.PathIn(steam.GameRoot), "bind F1 \"say hi\"\n");
        Assert.Null(autoexec.Apply(steam.Read(), [new ConfigLine("fps_max_ui", "60")]));
        string file = Path.Combine(steam.DataRoot, "копия настроек.zip");
        Directory.CreateDirectory(steam.DataRoot);

        SettingsBackupFile.Write(file, service.Take(steam.Read(), "1.2.3")!);
        SettingsBackup read = SettingsBackupFile.Read(file);

        Assert.Equal(
            [BackupPart.Graphics, BackupPart.Hotkeys, BackupPart.GameSettings, BackupPart.LaunchOptions, BackupPart.Autoexec, BackupPart.Builds],
            read.Parts);
        Assert.Equal("-novid -high", read.Manifest.LaunchOptions);
        Assert.Equal("demo_player", read.Manifest.Account);
        Assert.Equal("1.2.3", read.Manifest.AppVersion);
        Assert.True(read.FromThisComputer);
        Assert.Equal("bind F1 \"say hi\"\n", Encoding.Latin1.GetString(read.Autoexec!));
        Assert.Equal("guide", Encoding.UTF8.GetString(read.Files["remote/guides/axe.build"]));
        Assert.Equal(Settings.Replace("\n", "\r\n", StringComparison.Ordinal), Encoding.UTF8.GetString(read.Files["local/cfg/user_convars_0_slot0.vcfg"]));
        Assert.DoesNotContain("remote/cfg/stats.dat", read.Files.Keys);

        // A plain zip with the paths Steam uses, to open without DotaTune.
        using ZipArchive zip = ZipFile.OpenRead(file);
        Assert.Contains("570/local/cfg/video.txt", zip.Entries.Select(e => e.FullName));
        Assert.Contains("autoexec.cfg", zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public void Restore_puts_the_chosen_parts_back_and_undo_returns_the_state_before()
    {
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-novid");
        string keys = steam.WriteGameFile(Account, "remote/cfg/dotakeys_personal.lst", "keys mine");
        string video = steam.WriteGameFile(Account, "local/cfg/video.txt", "video mine");
        string convars = steam.WriteGameFile(Account, "remote/user_convars.vcfg", Settings);
        string autoexecPath = AutoexecService.PathIn(steam.GameRoot);
        (SettingsBackupService service, AutoexecService autoexec) = Services(steam);
        steam.Write(autoexecPath, "bind F1 a");
        Assert.Null(autoexec.Apply(steam.Read(), [new ConfigLine("fps_max_ui", "60")]));
        SettingsBackup backup = service.Take(steam.Read(), "1.2.3")!;

        // After the copy: other hotkeys, no video file, another line and other own lines.
        File.WriteAllText(keys, "keys changed");
        File.Delete(video);
        string otherSettings = Settings.Replace("хорошо", "иначе", StringComparison.Ordinal);
        File.WriteAllText(convars, otherSettings);
        steam.SetLaunchOptions("-high");
        Assert.Null(autoexec.ReplaceOwnText(steam.Read(), Encoding.Latin1.GetBytes("bind F2 b\n")));
        BackupPart[] chosen = [BackupPart.Hotkeys, BackupPart.Graphics, BackupPart.LaunchOptions, BackupPart.Autoexec];

        Assert.Equal(new BackupResult(null, 4), service.Restore(steam.Read(), backup, chosen));

        Assert.Equal("keys mine", File.ReadAllText(keys));
        Assert.Equal("video mine", File.ReadAllText(video));
        Assert.Equal(otherSettings, File.ReadAllText(convars));
        Assert.Equal("-novid", LaunchOptions.Read(steam.SteamRoot, Account));
        Assert.Equal("bind F1 a", Encoding.Latin1.GetString(autoexec.OwnText(steam.Read())!));
        Assert.Equal(AutoexecHealth.Ready, autoexec.Check());
        Assert.Equal(new BackupResult(null, 0), service.Restore(steam.Read(), backup, chosen));

        Assert.Equal(new BackupResult(null, 4), service.Undo(steam.Read()));

        Assert.Equal("keys changed", File.ReadAllText(keys));
        Assert.False(File.Exists(video));
        Assert.Equal("-high", LaunchOptions.Read(steam.SteamRoot, Account));
        Assert.Equal("bind F2 b\n", Encoding.Latin1.GetString(autoexec.OwnText(steam.Read())!));
        Assert.Equal(AutoexecHealth.Ready, autoexec.Check());
        Assert.Equal(BackupRefusal.NothingToUndo, service.Undo(steam.Read()).Refusal);

        // Taking the DotaTune block out after a restore leaves the own lines as they came back.
        Assert.Null(service.Restore(steam.Read(), backup, chosen).Refusal);
        Assert.Null(autoexec.Remove(steam.Read()));
        Assert.Equal("bind F1 a", File.ReadAllText(autoexecPath));
    }

    [Fact]
    public void Restore_refuses_while_the_game_or_steam_holds_the_files()
    {
        using var steam = new FakeSteam();
        steam.SetLaunchOptions("-novid");
        string keys = steam.WriteGameFile(Account, "remote/cfg/dotakeys_personal.lst", "keys mine");
        (SettingsBackupService service, _) = Services(steam);
        SettingsBackup backup = service.Take(steam.Read(), "1.2.3")!;
        File.WriteAllText(keys, "keys changed");
        steam.SetLaunchOptions("-high");

        steam.Running.Add("dota2");
        Assert.Equal(BackupRefusal.DotaRunning, service.Restore(steam.Read(), backup, backup.Parts).Refusal);
        steam.Running.Clear();

        steam.Running.Add("steam");
        Assert.True(SettingsBackupService.NeedsSteamClosed(steam.Read(), backup, backup.Parts));
        Assert.Equal(BackupRefusal.SteamRunning, service.Restore(steam.Read(), backup, backup.Parts).Refusal);
        Assert.Equal("keys changed", File.ReadAllText(keys));

        // Without the launch line Steam may stay open.
        Assert.Equal(new BackupResult(null, 1), service.Restore(steam.Read(), backup, [BackupPart.Hotkeys]));
        Assert.Equal("keys mine", File.ReadAllText(keys));
        Assert.Equal(BackupRefusal.NothingChosen, service.Restore(steam.Read(), backup, [BackupPart.Graphics]).Refusal);
    }

    [Fact]
    public void Restore_needs_the_account_folder_and_leaves_grids_the_game_would_not_read()
    {
        using var steam = new FakeSteam();
        (SettingsBackupService service, _) = Services(steam);
        var manifest = new BackupManifest(SettingsBackupFile.FormatVersion, SettingsBackupFile.Kind, "1.2.3", null, DateTimeOffset.Now, "другой", "0000", null);
        var backup = new SettingsBackup(manifest, new Dictionary<string, byte[]>
        {
            [TransferFiles.HotkeysPath] = Encoding.UTF8.GetBytes("keys from the other computer"),
            [TransferFiles.HeroGridPath] = Encoding.UTF8.GetBytes("""{"version":3,"configs":[]}"""),
        }, null);

        Assert.False(backup.FromThisComputer);
        Assert.Equal(BackupRefusal.NoGameFolder, service.Restore(steam.Read(), backup, backup.Parts).Refusal);

        steam.WriteGameFile(Account, "remote/cfg/chat.cfg", "chat");
        Assert.Equal(new BackupResult(null, 1, GridsSkipped: true), service.Restore(steam.Read(), backup, backup.Parts));
        Assert.False(File.Exists(Path.Combine(steam.AccountGameFolder(), "remote", "cfg", "hero_grid_config.json")));

        steam.SetGridSeen();
        Assert.Equal(new BackupResult(null, 1), service.Restore(steam.Read(), backup, backup.Parts));
        Assert.True(File.Exists(Path.Combine(steam.AccountGameFolder(), "remote", "cfg", "hero_grid_config.json")));
    }

    [Fact]
    public void Reading_takes_only_known_files_and_refuses_what_is_not_a_copy()
    {
        using var folder = new CyrillicTempFolder();
        string file = Path.Combine(folder.Path, "копия.zip");
        Zip(file, new()
        {
            ["manifest.json"] = """{"formatVersion":1,"kind":"dotatune-settings","appVersion":"1","createdAt":"2026-10-27T10:00:00+03:00","missing":["remote/cfg/chat.cfg","../evil.cfg"]}""",
            ["570/remote/cfg/dotakeys_personal.lst"] = "keys",
            ["570/remote/../../evil.cfg"] = "evil",
            ["570/remote/guides/../../x.build"] = "evil",
            ["570/remote/guides/axe.build"] = "guide",
            ["570/remote/cfg/stats.dat"] = "personal",
            ["notes.txt"] = "other",
        });

        SettingsBackup read = SettingsBackupFile.Read(file);

        Assert.Equal(["remote/cfg/dotakeys_personal.lst", "remote/guides/axe.build"], read.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["remote/cfg/chat.cfg"], read.Manifest.Missing);
        Assert.Equal([BackupPart.Hotkeys, BackupPart.Builds, BackupPart.Misc], read.Parts);

        Zip(file, new() { ["manifest.json"] = """{"formatVersion":2,"kind":"dotatune-settings","appVersion":"9","createdAt":"2026-10-27T10:00:00+03:00"}""" });
        Assert.Equal(PresetProblem.Newer, Assert.Throws<PresetException>(() => SettingsBackupFile.Read(file)).Problem);

        // A preset has a manifest.json too, without the kind of a copy.
        Zip(file, new() { ["manifest.json"] = """{"formatVersion":1,"appVersion":"1","createdAt":"2026-10-27T10:00:00+03:00","parts":["choice"]}""" });
        Assert.Equal(PresetProblem.NotAPreset, Assert.Throws<PresetException>(() => SettingsBackupFile.Read(file)).Problem);

        // Own lines with a DotaTune mark would double the block on restore.
        Zip(file, new()
        {
            ["manifest.json"] = """{"formatVersion":1,"kind":"dotatune-settings","appVersion":"1","createdAt":"2026-10-27T10:00:00+03:00"}""",
            ["autoexec.cfg"] = "bind F1 a\n// DotaTune begin\n",
        });
        Assert.Equal(PresetProblem.Damaged, Assert.Throws<PresetException>(() => SettingsBackupFile.Read(file)).Problem);

        Zip(file, new() { ["570/remote/cfg/dotakeys_personal.lst"] = "keys" });
        Assert.Equal(PresetProblem.NotAPreset, Assert.Throws<PresetException>(() => SettingsBackupFile.Read(file)).Problem);

        Zip(file, new() { ["manifest.json"] = "{", ["570/local/cfg/video.txt"] = new string(' ', SettingsBackupFile.MaxEntryBytes + 1) });
        Assert.Equal(PresetProblem.TooLarge, Assert.Throws<PresetException>(() => SettingsBackupFile.Read(file)).Problem);

        File.WriteAllText(file, "not a zip");
        Assert.Equal(PresetProblem.NotAPreset, Assert.Throws<PresetException>(() => SettingsBackupFile.Read(file)).Problem);
    }

    [Fact]
    public void Every_file_a_group_collects_is_known_to_that_group()
    {
        using var steam = new FakeSteam();
        string[] paths =
        [
            "remote/cfg/dotakeys_personal.lst", "remote/user_convars.vcfg", "local/cfg/user_convars_0_slot0.vcfg",
            "local/cfg/user_convars_0_slot0.vcfg_lastclouded", "remote/user_keys.vcfg", "local/cfg/user_keys_0_slot0.vcfg",
            "local/cfg/user_keys_0_slot0.vcfg_lastclouded", "local/cfg/user_keys_0_slot3.vcfg_lastclouded", "remote/cfg/hero_grid_config.json",
            "remote/cfg/herobuilds.cfg", "remote/cfg/hero_facet_config.cfg", "remote/guides/lina.build", "remote/scripts/item_suggest_preference.txt",
            "remote/scripts/control_groups.txt", "remote/cfg/chat.cfg", "remote/scripts/lobby_settings.txt", "local/cfg/video.txt",
            "local/cfg/machine_convars.vcfg",
        ];
        foreach (string path in paths)
        {
            steam.WriteGameFile(Account, path, "x");
        }

        foreach (TransferGroup group in TransferFiles.All)
        {
            foreach ((string path, _) in TransferFiles.Collect(steam.AccountGameFolder(), [group]))
            {
                Assert.Equal(group, TransferFiles.GroupOf(path));
            }
        }

        Assert.Equal(paths.Order(StringComparer.Ordinal), TransferFiles.Collect(steam.AccountGameFolder(), TransferFiles.All).Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Null(TransferFiles.GroupOf("remote/cfg/stats.dat"));
        Assert.Null(TransferFiles.GroupOf("remote/guides/sub/x.build"));
        Assert.Null(TransferFiles.GroupOf("remote/guides/.build"));
    }

    private static (SettingsBackupService, AutoexecService) Services(FakeSteam steam)
    {
        var log = new FileLog(Path.Combine(steam.DataRoot, "logs"));
        var autoexec = new AutoexecService(new AutoexecStateStore(Path.Combine(steam.DataRoot, "autoexec.json"), log), log);
        return (new SettingsBackupService(Path.Combine(steam.DataRoot, "before-restore.zip"), autoexec, log), autoexec);
    }

    private static void Zip(string file, Dictionary<string, string> entries)
    {
        File.Delete(file);
        using ZipArchive zip = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach ((string name, string text) in entries)
        {
            using Stream entry = zip.CreateEntry(name).Open();
            entry.Write(Encoding.UTF8.GetBytes(text));
        }
    }
}
