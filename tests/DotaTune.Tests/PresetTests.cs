using System.IO.Compression;
using System.Text;
using DotaTune.Core.Config;
using DotaTune.Core.Effects;
using DotaTune.Core.Presets;

namespace DotaTune.Tests;

public class PresetTests
{
    private const string Keys = "\"config\"\n{\n\t\"bindings\"\n\t{\n\t\t\"\\\"\t\t\"toggleconsole\"\n\t\t\"F1\"\t\t\"say_team хорошо\"\n\t}\n}\n";

    private static readonly PresetChoice Full = new()
    {
        Effects = ["Weather", "MapAmbience", "BuildingAmbience", "FogMist", "EventCosmetics", "CosmeticAmbience"],
        Config = new GameConfig
        {
            FriendlyColor = new Rgb(0, 255, 255),
            EnemyColor = new Rgb(255, 0, 255),
            NeutralColor = new Rgb(255, 140, 0),
            CreepScale = 1.5,
            HideFrontpageVideo = true,
            MenuFpsLimit = 144,
            NoParticleLights = true,
        },
        Launch = ["-novid", "-nojoy"],
    };

    [Fact]
    public void Code_goes_there_and_back_and_survives_chat_line_breaks()
    {
        string code = PresetCode.Encode(Full);

        Assert.StartsWith(PresetCode.Prefix, code, StringComparison.Ordinal);
        Assert.True(code.Length < 400, $"{code.Length} characters");
        PresetChoice back = PresetCode.Decode(code[..20] + "\n  " + code[20..] + " ");
        Assert.Equal(Full.Effects, back.Effects);
        Assert.Equal(Full.Config, back.Config);
        Assert.Equal(Full.Launch, back.Launch);
        Assert.Null(PresetCode.Decode(PresetCode.Encode(new PresetChoice())).Effects);
    }

    [Theory]
    [InlineData("hello", PresetProblem.NotAPreset)]
    [InlineData("DT0-AAAA", PresetProblem.NotAPreset)]
    [InlineData("DT2-AAAAAAAA", PresetProblem.Newer)]
    [InlineData("DT1-A", PresetProblem.Damaged)]
    [InlineData("DT1-AAAAAAAAAAAA", PresetProblem.Damaged)]
    public void Bad_codes_are_refused_with_a_reason(string code, PresetProblem problem) =>
        Assert.Equal(problem, Assert.Throws<PresetException>(() => PresetCode.Decode(code)).Problem);

    [Fact]
    public void A_changed_or_cut_code_is_damaged()
    {
        string code = PresetCode.Encode(Full);
        int middle = code.Length / 2;
        string changed = code[..middle] + (code[middle] == 'A' ? 'B' : 'A') + code[(middle + 1)..];

        Assert.Equal(PresetProblem.Damaged, Assert.Throws<PresetException>(() => PresetCode.Decode(changed)).Problem);
        Assert.Equal(PresetProblem.Damaged, Assert.Throws<PresetException>(() => PresetCode.Decode(code[..^6])).Problem);
    }

    [Fact]
    public void File_keeps_the_choice_and_the_parts_written_anew()
    {
        using var folder = new CyrillicTempFolder();
        string file = Path.Combine(folder.Path, "набор.dotatune");
        var parts = new Dictionary<PresetPart, byte[]>
        {
            [PresetPart.HeroGrids] = Encoding.UTF8.GetBytes("""{"version":3,"configs":[{"config_name":"Мои","categories":[]}]}"""),
            [PresetPart.Hotkeys] = PresetFile.Normalize(PresetPart.Hotkeys, Encoding.UTF8.GetBytes(Keys)),
        };

        PresetFile.Write(file, new PresetManifest(1, "0.1.0", "6967", DateTimeOffset.UnixEpoch, ["choice", "HeroGrids", "Hotkeys"]), Full, parts);
        PresetPackage package = PresetFile.Read(file);

        Assert.Equal("6967", package.Manifest!.GameBuild);
        Assert.Equal(Full.Config, package.Choice.Config);
        Assert.Equal([PresetPart.HeroGrids, PresetPart.Hotkeys], package.Parts.Keys.Order());
        string keys = Encoding.UTF8.GetString(package.Parts[PresetPart.Hotkeys]);
        Assert.Contains("\"\\\"", keys, StringComparison.Ordinal);
        Assert.Contains("say_team хорошо", keys, StringComparison.Ordinal);
        Assert.Contains("\"Мои\"", Encoding.UTF8.GetString(package.Parts[PresetPart.HeroGrids]), StringComparison.Ordinal);
    }

    [Fact]
    public void File_takes_only_its_own_names_and_caps_sizes()
    {
        using var folder = new CyrillicTempFolder();

        Assert.Equal(PresetProblem.NotAPreset, Refused(Zip(("manifest.json", Manifest(1)), ("../choice.json", "{}"))));
        Assert.Equal(PresetProblem.Newer, Refused(Zip(("manifest.json", Manifest(2)))));
        Assert.Equal(PresetProblem.NotAPreset, Refused(Zip(("choice.json", "{}"))));
        Assert.Equal(PresetProblem.Damaged, Refused(Zip(("manifest.json", Manifest(1)), ("user_convars.vcfg", "\"config\"\n{\n"))));
        Assert.Equal(PresetProblem.TooLarge, Refused(Zip(("manifest.json", Manifest(1)), ("dotakeys_personal.lst", new string('a', PresetFile.MaxEntryBytes + 1)))));
        string notZip = Path.Combine(folder.Path, "не-zip.dotatune");
        File.WriteAllText(notZip, "plain text");
        Assert.Equal(PresetProblem.NotAPreset, Assert.Throws<PresetException>(() => PresetFile.Read(notZip)).Problem);

        PresetPackage package = PresetFile.Read(Zip(("manifest.json", Manifest(1)), ("run.exe", "MZ"), ("choice.json", """{"launch":["-novid","-autoconfig"]}""")));
        Assert.Empty(package.Parts);
        Assert.Equal(["-novid", "-autoconfig"], package.Choice.Launch);

        string Zip(params (string Name, string Text)[] entries)
        {
            string path = Path.Combine(folder.Path, $"{Guid.NewGuid():N}.dotatune");
            using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
            foreach ((string name, string text) in entries)
            {
                using Stream stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }

            return path;
        }

        static string Manifest(int version) =>
            $$"""{"formatVersion":{{version}},"appVersion":"0.1.0","createdAt":"2026-10-26T00:00:00+00:00","parts":[]}""";

        static PresetProblem Refused(string path) => Assert.Throws<PresetException>(() => PresetFile.Read(path)).Problem;
    }

    [Fact]
    public void Review_sets_aside_what_this_version_does_not_know()
    {
        PresetReview review = PresetReview.Of(new PresetChoice
        {
            Effects = ["Weather", "Weather", "3", "Weather, Sprays", "RainOfFrogs"],
            Config = new GameConfig { CreepScale = 40, MenuFpsLimit = 5, HideFrontpageVideo = true },
            Launch = ["-NOVID", "-autoconfig", "+exec evil"],
        });

        Assert.Equal([EffectGroup.Weather], review.Effects);
        Assert.Equal(["3", "Weather, Sprays", "RainOfFrogs"], review.UnknownEffects);
        Assert.Equal(new GameConfig { HideFrontpageVideo = true }, review.Config);
        Assert.Equal(["-novid"], review.Launch);
        Assert.Equal(["-autoconfig", "+exec evil"], review.UnknownLaunch);
        Assert.Null(PresetReview.Of(new PresetChoice()).Effects);
        Assert.Equal(["-nojoy"], PresetReview.Choice(null, null, "-high -nojoy -prewarm").Launch);
    }

    [Fact]
    public void Parts_become_the_account_files()
    {
        using var steam = new FakeSteam();
        string folder = steam.AccountGameFolder();
        steam.WriteGameFile(FakeSteam.AccountId, TransferFiles.HeroGridPath, """{"version":3,"configs":[{"config_name":"Свои","categories":[]}]}""");
        var parts = new Dictionary<PresetPart, byte[]>
        {
            [PresetPart.HeroGrids] = Encoding.UTF8.GetBytes("""{"version":3,"configs":[{"config_name":"Чужие","categories":[]}]}"""),
            [PresetPart.GameSettings] = Encoding.UTF8.GetBytes("\"config\"\n{\n}\n"),
        };

        IReadOnlyList<(string Path, byte[] Content)> files = PresetFile.TargetFiles(folder, parts);

        Assert.Equal(
            [TransferFiles.HeroGridPath, "remote/user_convars.vcfg", "local/cfg/user_convars_0_slot0.vcfg", "local/cfg/user_convars_0_slot0.vcfg_lastclouded"],
            files.Select(f => f.Path));
        string grids = Encoding.UTF8.GetString(files[0].Content);
        Assert.Contains("Свои", grids, StringComparison.Ordinal);
        Assert.Contains("Чужие #DotaTune", grids, StringComparison.Ordinal);
        Assert.Equal("\"config\"\r\n{\r\n}\r\n", Encoding.UTF8.GetString(files[2].Content));
    }
}
