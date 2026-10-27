using System.Text;
using System.Text.Json;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Game;
using DotaTune.Core.Pack;
using ValvePak;

namespace DotaTune.Tests;

public sealed class PackServiceTests : IDisposable
{
    private readonly FakeSteam _steam = new();
    private readonly InstallStateStore _store;
    private readonly PackService _service;
    private readonly byte[] _pak;

    public PackServiceTests()
    {
        var log = new FileLog(Path.Combine(_steam.DataRoot, "logs"));
        _store = new InstallStateStore(Path.Combine(_steam.DataRoot, "install.json"), log);
        _service = new PackService(_store, log);
        _pak = PackBuilder.Build(new PackMarker(PackBuilder.AppName, "0.1.0", "6967", DateTimeOffset.UnixEpoch), new Dictionary<string, byte[]>());
    }

    private string Boot => BootConfig.PathIn(_steam.GameRoot);

    private string RussianPak(int slot) => Path.Combine(_steam.GameRoot, "game", "dota_russian", InstallState.PakFileName(slot));

    public void Dispose() => _steam.Dispose();

    [Fact]
    public void Built_pak_carries_the_marker()
    {
        string file = Path.Combine(_steam.DataRoot, "pak.vpk");
        Directory.CreateDirectory(_steam.DataRoot);
        File.WriteAllBytes(file, _pak);

        using var package = new Package();
        package.Read(file);
        PackageEntry entry = package.FindEntry(LanguageFolders.MarkerPath)!;
        package.ReadEntry(entry, out byte[] json, validateCrc: true);

        using JsonDocument marker = JsonDocument.Parse(json);
        Assert.Equal("DotaTune", marker.RootElement.GetProperty("app").GetString());
        Assert.Equal("6967", marker.RootElement.GetProperty("gameBuild").GetString());
    }

    [Fact]
    public void English_voices_get_the_carrier_and_get_it_back_on_remove()
    {
        byte[] bootBefore = File.ReadAllBytes(Boot);

        InstallResult result = _service.Install(_steam.Read, _pak);

        Assert.True(result.Installed);
        Assert.Equal("russian", result.State!.Language);
        Assert.Equal(40, result.State.Slot);
        Assert.Equal("english", result.State.PreviousAudioLanguage);
        Assert.Equal(_pak, File.ReadAllBytes(RussianPak(40)));
        Assert.Equal("\"boot\"\n{\n\t\"AudioLanguage\"\t\t\"russian\"\n\t\"UILanguage\"\t\t\"english\"\n}\n", File.ReadAllText(Boot));

        GameState after = _steam.Read();
        Assert.Equal(new PakFile(40, "pak40_dir.vpk", PakOwner.DotaTune), after.Folders.Single(f => f.Language == "russian").Paks.Single());
        Assert.Equal(PackHealth.Ready, _service.Check(after));

        _service.Remove(after);

        Assert.False(File.Exists(RussianPak(40)));
        Assert.Equal(bootBefore, File.ReadAllBytes(Boot));
        Assert.Null(_store.Load());
        Assert.Equal(PackHealth.NotInstalled, _service.Check(_steam.Read()));
    }

    [Fact]
    public void Russian_voices_are_left_alone()
    {
        _steam.SetBoot("russian");
        byte[] bootBefore = File.ReadAllBytes(Boot);

        InstallResult result = _service.Install(_steam.Read, _pak);
        _service.Remove(_steam.Read());

        Assert.Null(result.State!.PreviousAudioLanguage);
        Assert.Equal(bootBefore, File.ReadAllBytes(Boot));
    }

    [Fact]
    public void Taken_slot_moves_the_pack_and_the_other_file_survives()
    {
        _steam.AddPak("russian", 40, withMarker: false);
        byte[] foreign = File.ReadAllBytes(RussianPak(40));

        InstallResult result = _service.Install(_steam.Read, _pak);
        _service.Remove(_steam.Read());

        Assert.Equal(41, result.State!.Slot);
        Assert.Equal(foreign, File.ReadAllBytes(RussianPak(40)));
        Assert.False(File.Exists(RussianPak(41)));
    }

    [Fact]
    public void Nothing_is_written_while_dota_runs()
    {
        _steam.Running.Add("dota2");
        byte[] bootBefore = File.ReadAllBytes(Boot);

        InstallResult result = _service.Install(_steam.Read, _pak);

        Assert.Equal(InstallRefusal.DotaRunning, result.Refusal);
        Assert.False(File.Exists(RussianPak(40)));
        Assert.Equal(bootBefore, File.ReadAllBytes(Boot));
    }

    [Fact]
    public void English_launch_option_installs_nothing()
    {
        _steam.SetLaunchOptions("-language english");

        Assert.Equal(InstallRefusal.NothingToDo, _service.Install(_steam.Read, _pak).Refusal);
        Assert.Null(_store.Load());
    }

    [Fact]
    public void Second_install_keeps_one_pack_and_the_original_voice()
    {
        _service.Install(_steam.Read, _pak);
        InstallResult again = _service.Install(_steam.Read, _pak);

        Assert.Equal("english", again.State!.PreviousAudioLanguage);
        Assert.Single(_steam.Read().Folders.Single(f => f.Language == "russian").Paks);

        _service.Remove(_steam.Read());
        Assert.Equal("english", BootConfig.Read(_steam.GameRoot)!.AudioLanguage);
    }

    [Fact]
    public void Voice_changed_by_the_person_is_not_reverted()
    {
        _service.Install(_steam.Read, _pak);
        _steam.SetBoot("schinese");

        Assert.Equal(PackHealth.FolderNotLoaded, _service.Check(_steam.Read()));
        _service.Remove(_steam.Read());

        Assert.Equal("schinese", BootConfig.Read(_steam.GameRoot)!.AudioLanguage);
        Assert.False(File.Exists(RussianPak(40)));
    }

    [Fact]
    public void Health_sees_a_missing_a_changed_pack_and_a_new_build()
    {
        _service.Install(_steam.Read, _pak);

        _steam.Write(Path.Combine(_steam.GameRoot, "game", "dota", "steam.inf"), "ClientVersion=6968\n");
        Assert.Equal(PackHealth.OtherBuild, _service.Check(_steam.Read()));

        File.WriteAllBytes(RussianPak(40), [1, 2, 3]);
        Assert.Equal(PackHealth.Changed, _service.Check(_steam.Read()));

        _service.Remove(_steam.Read());
        Assert.True(File.Exists(RussianPak(40)), "a file that is not ours must stay");
    }

    [Fact]
    public void Missing_pack_is_reported()
    {
        _service.Install(_steam.Read, _pak);
        File.Delete(RussianPak(40));

        Assert.Equal(PackHealth.Missing, _service.Check(_steam.Read()));
    }

    [Fact]
    public void Boot_editor_keeps_every_other_byte()
    {
        string original = "\"boot\"\r\n{\r\n\t\"UILanguage\"\t\t\"russian\"\r\n\t\"AudioLanguage\"  \"english\"\r\n}\r\n";
        File.WriteAllBytes(Boot, Encoding.ASCII.GetBytes(original));

        BootConfigEditor.SetAudioLanguage(Boot, "koreana");

        Assert.Equal(original.Replace("\"english\"", "\"koreana\"", StringComparison.Ordinal), File.ReadAllText(Boot));
    }

    [Fact]
    public void Boot_editor_refuses_a_file_without_the_key()
    {
        _steam.SetBoot("english", withKey: false);

        Assert.Throws<InvalidDataException>(() => BootConfigEditor.SetAudioLanguage(Boot, "russian"));
    }
}
