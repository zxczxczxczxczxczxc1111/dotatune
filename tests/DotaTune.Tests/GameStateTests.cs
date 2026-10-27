using DotaTune.Core.Game;
using DotaTune.Core.Steam;

namespace DotaTune.Tests;

public class GameStateTests
{
    [Theory]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    public void English_voices_switch_to_the_russian_carrier(string culture)
    {
        using var scope = new CultureScope(culture);
        using var steam = new FakeSteam();

        GameState state = steam.Read();

        Assert.Null(state.Problem);
        Assert.Equal(steam.SteamRoot, state.Steam!.Root);
        Assert.Contains(steam.Library, state.Steam.Libraries);
        Assert.Equal(steam.GameRoot, state.Dota!.GameRoot);
        Assert.Equal("6967", state.Dota.Build);
        Assert.Equal(new SteamAccount(FakeSteam.AccountId, "demo_player", SignedInNow: true), state.Steam.Account);
        Assert.Equal("english", state.Boot!.AudioLanguage);
        Assert.Equal(["koreana", "russian", "schinese"], state.Folders.Select(f => f.Language));
        Assert.Equal(new PackDecision(PackAction.SwitchToCarrier, null, "russian"), state.Decision);
    }

    [Fact]
    public void Russian_voices_use_their_own_folder()
    {
        using var steam = new FakeSteam();
        steam.SetBoot("russian");

        Assert.Equal(new PackDecision(PackAction.UseVoiceFolder, "russian", "russian"), steam.Read().Decision);
    }

    [Fact]
    public void Voice_language_without_a_folder_is_treated_like_english()
    {
        using var steam = new FakeSteam();
        steam.SetBoot("brazilian");

        Assert.Equal(new PackDecision(PackAction.SwitchToCarrier, null, "russian"), steam.Read().Decision);
    }

    [Fact]
    public void A_carrier_with_a_voice_pack_is_skipped()
    {
        using var steam = new FakeSteam();
        steam.Write(Path.Combine(steam.GameRoot, "game", "dota_russian", "pak01_dir.vpk"), "valve");

        GameState state = steam.Read();

        Assert.True(state.Folders.Single(f => f.Language == "russian").HasVoicePack);
        Assert.Equal(new PackDecision(PackAction.SwitchToCarrier, null, "schinese"), state.Decision);
    }

    [Fact]
    public void No_carrier_when_every_folder_has_voices()
    {
        using var steam = new FakeSteam();
        foreach (string language in PackPlanner.Carriers)
        {
            steam.Write(Path.Combine(steam.GameRoot, "game", $"dota_{language}", "pak01_dir.vpk"), "valve");
        }

        Assert.Equal(PackAction.NoCarrier, steam.Read().Decision!.Action);
    }

    [Theory]
    [InlineData("-novid -language english", PackAction.OfferRemoveEnglishOverride, null, null)]
    [InlineData("-language \"schinese\" -novid", PackAction.UseLaunchLanguageFolder, "schinese", "schinese")]
    [InlineData("-LANGUAGE Russian", PackAction.UseLaunchLanguageFolder, "russian", "russian")]
    [InlineData("-language dutch", PackAction.OfferRemoveForeignOverride, "dutch", null)]
    [InlineData("-language portuguese", PackAction.OfferRemoveForeignOverride, null, null)]
    public void Language_launch_option_decides(string options, PackAction action, string? loaded, string? target)
    {
        using var steam = new FakeSteam();
        steam.SetBoot("russian");
        steam.Write(Path.Combine(steam.GameRoot, "game", "dota_dutch", "pak66_dir.vpk"), "minify");
        steam.SetLaunchOptions(options);

        GameState state = steam.Read();

        Assert.Equal(options, state.LaunchOptions);
        Assert.Equal(new PackDecision(action, loaded, target), state.Decision);
    }

    [Fact]
    public void Missing_boot_config_asks_to_start_the_game_once()
    {
        using var steam = new FakeSteam();
        steam.SetBoot(null);

        Assert.Equal(PackAction.StartGameOnce, steam.Read().Decision!.Action);
    }

    [Fact]
    public void Boot_config_without_the_voice_key_stops()
    {
        using var steam = new FakeSteam();
        steam.SetBoot("english", withKey: false);

        Assert.Equal(PackAction.UnknownBootFormat, steam.Read().Decision!.Action);
    }

    [Fact]
    public void Closed_steam_takes_the_most_recent_account()
    {
        using var steam = new FakeSteam { ActiveUser = 0 };

        Assert.Equal(new SteamAccount(FakeSteam.AccountId, "demo_player", SignedInNow: false), steam.Read().Steam!.Account);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Closed_steam_takes_the_account_from_the_current_login_format(bool withAutoLogin)
    {
        // With AutoLogin Steam signs that account in even when another one logged in later;
        // without it the latest timestamp tells who it was.
        using var steam = new FakeSteam { ActiveUser = 0 };
        steam.UseCurrentLoginFormat(withAutoLogin);

        Assert.Equal(new SteamAccount(FakeSteam.AccountId, "demo_player", SignedInNow: false), steam.Read().Steam!.Account);
        Assert.Null(steam.Read().Problem);
    }

    [Fact]
    public void Closed_steam_takes_the_auto_login_user_from_the_registry_first()
    {
        // The file still marks demo_player for AutoLogin, the registry already names the other account.
        using var steam = new FakeSteam { ActiveUser = 0, AutoLoginUser = "ДРУГОЙ" };
        steam.UseCurrentLoginFormat();

        Assert.Equal(new SteamAccount(111111111, "другой", SignedInNow: false), steam.Read().Steam!.Account);
    }

    [Fact]
    public void Paks_are_told_apart_by_the_marker()
    {
        using var steam = new FakeSteam();
        steam.AddPak("russian", 40, withMarker: true);
        steam.AddPak("russian", 66, withMarker: false);

        LanguageFolder russian = steam.Read().Folders.Single(f => f.Language == "russian");

        Assert.Equal(
            [new PakFile(40, "pak40_dir.vpk", PakOwner.DotaTune), new PakFile(66, "pak66_dir.vpk", PakOwner.Other)],
            russian.Paks);
        Assert.False(russian.HasVoicePack);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("noise")]
    [InlineData("half")]
    [InlineData("tree")]
    [InlineData("sizes")]
    [InlineData("terminator")]
    public void Broken_foreign_pak_is_foreign_and_the_game_still_reads(string damage)
    {
        using var steam = new FakeSteam();
        steam.AddPak("russian", 64, withMarker: false);
        string file = Path.Combine(steam.GameRoot, "game", "dota_russian", "pak64_dir.vpk");
        File.WriteAllBytes(file, Damaged(File.ReadAllBytes(file), damage));

        GameState state = steam.Read();

        Assert.Null(state.Problem);
        Assert.Equal([new PakFile(64, "pak64_dir.vpk", PakOwner.Other)], state.Folders.Single(f => f.Language == "russian").Paks);
    }

    /// <summary>A pak written by <see cref="FakeSteam.AddPak"/> without the marker, damaged one way.</summary>
    internal static byte[] Damaged(byte[] good, string damage)
    {
        return damage switch
        {
            "empty" => [],
            "noise" => [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 37 % 251))],
            "half" => good[..(good.Length / 2)],
            // VPK 2 header kept, the directory tree overwritten: names without their ends.
            "tree" => [.. good[..28], .. Enumerable.Repeat((byte)0xFF, good.Length - 28)],
            // Signature and version kept, the tree size near 2 GB.
            "sizes" => [.. good[..8], 0xF0, 0xFF, 0xFF, 0x7F, .. good[12..]],
            // The 0xFFFF that ends the entry of cfg/probe.cfg: ValvePak 7.2 throws FormatException.
            "terminator" => EntryEnd(good),
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };

        static byte[] EntryEnd(byte[] good)
        {
            byte[] bytes = (byte[])good.Clone();
            // After "probe\0": CRC 4, preload size 2, archive 2, offset 4, length 4, then the terminator.
            int end = bytes.AsSpan().IndexOf("probe\0"u8) + 6 + 16;
            Assert.Equal([0xFF, 0xFF], bytes[end..(end + 2)]);
            bytes[end + 1] = 0x2F;
            return bytes;
        }
    }

    [Fact]
    public void Running_programs_are_reported()
    {
        using var steam = new FakeSteam();
        steam.Running.Add("dota2");

        GameState state = steam.Read();

        Assert.True(state.DotaRunning);
        Assert.False(state.SteamRunning);
    }

    [Fact]
    public void No_steam()
    {
        using var steam = new FakeSteam();
        Directory.Delete(steam.SteamRoot, recursive: true);

        Assert.Equal(GameProblem.SteamNotFound, steam.Read().Problem);
    }

    [Fact]
    public void No_dota()
    {
        using var steam = new FakeSteam();
        File.Delete(Path.Combine(steam.Library, "steamapps", "appmanifest_570.acf"));

        GameState state = steam.Read();

        Assert.Equal(GameProblem.DotaNotFound, state.Problem);
        Assert.NotNull(state.Steam);
    }

    [Fact]
    public void Broken_file_is_reported_not_thrown()
    {
        using var steam = new FakeSteam();
        steam.Write(Path.Combine(steam.SteamRoot, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n");

        GameState state = steam.Read();

        Assert.Equal(GameProblem.UnreadableFile, state.Problem);
        Assert.EndsWith("libraryfolders.vdf", state.ProblemDetail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("-novid  -high", new[] { "-novid", "-high" })]
    [InlineData("-language \"russian\" +fps_max 0", new[] { "-language", "russian", "+fps_max", "0" })]
    [InlineData("+exec \"my cfg.cfg\"", new[] { "+exec", "my cfg.cfg" })]
    public void Launch_options_are_split_like_the_game_does(string options, string[] expected) =>
        Assert.Equal(expected, LaunchOptions.Tokenize(options));

    [Fact]
    public void Registry_path_is_normalized() =>
        Assert.Equal(@"Q:\No Such\Steam", SteamLocator.NormalizePath("Q:/No Such/Steam/"));

    [Fact]
    public void Lower_case_registry_path_gets_the_real_case()
    {
        using var steam = new FakeSteam();

        Assert.Equal(steam.SteamRoot, SteamLocator.NormalizePath(steam.SteamPath!));
    }
}
