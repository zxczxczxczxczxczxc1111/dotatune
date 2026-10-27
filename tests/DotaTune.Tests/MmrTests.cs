using System.Text;
using System.Text.Json;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Effects;
using DotaTune.Core.Game;
using DotaTune.Core.Mmr;
using DotaTune.Core.Pack;
using DotaTune.Core.Steam;
using ValvePak;

namespace DotaTune.Tests;

public sealed class MmrTests : IDisposable
{
    private const uint Account = FakeSteam.AccountId;
    private const long At = 1_793_070_000;
    private readonly FakeSteam _steam = new();
    private readonly FileLog _log;

    public MmrTests() => _log = new FileLog(Path.Combine(_steam.DataRoot, "logs"));

    /// <summary>The root menu layout of build 6967 as the game has it, compiled by build\panorama.ps1.</summary>
    public static byte[] ValveBase() => File.ReadAllBytes(Path.Combine(RepoPaths.Root, "tests", "DotaTune.Tests", "Fixtures", "valve_base.vxml_c"));

    /// <summary>A line the menu script prints, as console.log has it.</summary>
    public static string Line(string json) => $"10/27 07:30:12 [PanoramaScript] DOTATUNE_MMR {json}\r\n";

    public static string Reading(uint account, int mmr, int? delta, int? games, string? top, long at) =>
        Line(JsonSerializer.Serialize(new { v = 1, account, mmr, delta, games, top, at }));

    public void Dispose() => _steam.Dispose();

    [Fact]
    public void Log_gives_every_whole_reading_and_skips_the_rest()
    {
        string text = string.Concat(
            "10/27 07:29:00 [Filesystem] GAME \"D:\\game\\dota\\\" (path) \r\n",
            Reading(Account, 3450, null, null, "Lina|Win|27.10", At),
            Line("{\"v\":1,\"account\":222222222,\"mmr\":34"),
            Line("{\"v\":2,\"account\":222222222,\"mmr\":3475,\"at\":1793070100}"),
            Line("{\"v\":1,\"account\":222222222,\"mmr\":99999,\"at\":1793070100}"),
            Line("{\"v\":1,\"account\":222222222,\"mmr\":3475,\"at\":17}"),
            "10/27 07:31:00 [PanoramaScript] something else {\"v\":1}\r\n",
            Reading(Account, 3475, 25, 1, "Axe|Win|27.10", At + 100).Replace("\r\n", "\n", StringComparison.Ordinal));

        IReadOnlyList<MmrReading> readings = MmrLog.Parse(text);

        Assert.Equal(
            [new MmrReading(Account, 3450, null, null, "Lina|Win|27.10", At), new MmrReading(Account, 3475, 25, 1, "Axe|Win|27.10", At + 100)],
            readings);
    }

    [Fact]
    public void History_keeps_each_reading_once_and_the_newest_per_account()
    {
        var store = new MmrHistoryStore(Path.Combine(_steam.DataRoot, "mmr.json"), _log);
        var first = new MmrReading(Account, 3450, null, null, "a", At);
        var second = new MmrReading(Account, 3475, 25, 1, "b", At + 100);
        var other = new MmrReading(FakeSteam.OtherAccountId, 5000, null, null, null, At + 50);

        Assert.Equal(2, store.Add([first, other]));
        Assert.Equal(1, store.Add([first, second, other]));
        Assert.Equal(0, store.Add([second]));

        Assert.Equal([first, other, second], store.Load());
        Assert.Equal([second, other], MmrHistoryStore.Latest(store.Load()).OrderBy(r => r.Mmr));
        // No BOM: the file starts with the bracket of the list.
        Assert.Equal((byte)'[', File.ReadAllBytes(Path.Combine(_steam.DataRoot, "mmr.json"))[0]);
    }

    [Fact]
    public void History_drops_the_oldest_readings_of_an_account_past_the_limit()
    {
        var store = new MmrHistoryStore(Path.Combine(_steam.DataRoot, "mmr.json"), _log);
        var other = new MmrReading(FakeSteam.OtherAccountId, 5000, null, null, null, At - 10);
        store.Add([other, .. Enumerable.Range(0, MmrHistoryStore.KeptPerAccount + 5).Select(i => new MmrReading(Account, 3000 + i, 1, 1, null, At + i))]);

        IReadOnlyList<MmrReading> kept = store.Load();
        Assert.Equal(MmrHistoryStore.KeptPerAccount + 1, kept.Count);
        Assert.Contains(other, kept);
        Assert.Equal(3005, kept.Where(r => r.Account == Account).Min(r => r.Mmr));
    }

    [Fact]
    public void Broken_history_is_reported_and_left_as_it_is()
    {
        string file = Path.Combine(_steam.DataRoot, "mmr.json");
        Directory.CreateDirectory(_steam.DataRoot);
        File.WriteAllText(file, "[{\"account\":");
        var store = new MmrHistoryStore(file, _log);

        Assert.Throws<InvalidDataException>(store.Load);
        Assert.Throws<InvalidDataException>(() => store.Add([new MmrReading(Account, 3450, null, null, null, At)]));
        Assert.Equal("[{\"account\":", File.ReadAllText(file));
    }

    [Fact]
    public void Data_file_holds_the_readings_as_ascii_script_text()
    {
        byte[] file = MmrMenu.DataFile([new MmrReading(Account, 3475, -25, 2, "Лина|Поражение", At)], "ru");

        string text = Encoding.UTF8.GetString(CompiledResource.Data(file));
        Assert.StartsWith("var DotaTuneMmrData = {", text, StringComparison.Ordinal);
        Assert.EndsWith("};\n", text, StringComparison.Ordinal);
        Assert.All(text, c => Assert.True(c < 0x80, $"'{c}' is not ASCII"));

        using JsonDocument json = JsonDocument.Parse(text["var DotaTuneMmrData = ".Length..^2]);
        Assert.Equal(1, json.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("ru", json.RootElement.GetProperty("lang").GetString());
        JsonElement reading = json.RootElement.GetProperty("accounts").GetProperty("222222222");
        Assert.Equal(3475, reading.GetProperty("mmr").GetInt32());
        Assert.Equal(-25, reading.GetProperty("delta").GetInt32());
        Assert.Equal("Лина|Поражение", reading.GetProperty("top").GetString());
    }

    [Fact]
    public void Pack_files_are_the_layout_the_script_and_the_data()
    {
        Dictionary<string, byte[]> files = MmrMenu.PackFiles([], "en");

        Assert.Equal([MmrMenu.BasePath, MmrMenu.ScriptPath, MmrMenu.DataPath], files.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("DotaTuneMmrData", Encoding.UTF8.GetString(CompiledResource.Data(files[MmrMenu.ScriptPath])), StringComparison.Ordinal);
        Assert.Contains("\"accounts\":{}", Encoding.UTF8.GetString(CompiledResource.Data(files[MmrMenu.DataPath])), StringComparison.Ordinal);
        Assert.NotNull(CompiledResource.Block(files[MmrMenu.BasePath], "LaCo"u8));
    }

    [Fact]
    public void Our_layout_fits_the_game_layout_of_build_6967_only()
    {
        Assert.Null(MmrMenu.Problem(ValveBase()));

        // A game layout that already loads scripts is not the one ours is made from.
        string? withScripts = MmrMenu.Problem(MmrMenu.PackFiles([], "en")[MmrMenu.BasePath]);
        Assert.NotNull(withScripts);
        Assert.Contains("SCRIPTS", withScripts, StringComparison.Ordinal);

        Assert.StartsWith("the game's root layout is not readable", MmrMenu.Problem([1, 2, 3]), StringComparison.Ordinal);
    }

    [Fact]
    public void Game_layout_is_read_from_the_archive()
    {
        GameFiles.WritePak(_steam.Pak01, new Dictionary<string, byte[]>(GameFiles.Default()) { [MmrMenu.BasePath] = ValveBase() });

        Assert.Equal(ValveBase(), MmrMenu.ReadGameBase(Path.GetDirectoryName(_steam.Pak01)!));

        GameFiles.WritePak(_steam.Pak01, GameFiles.Default());
        Assert.Throws<InvalidDataException>(() => MmrMenu.ReadGameBase(Path.GetDirectoryName(_steam.Pak01)!));
    }

    [Fact]
    public void Collect_takes_new_readings_from_the_console_log()
    {
        var packs = new PackService(new InstallStateStore(Path.Combine(_steam.DataRoot, "install.json"), _log), _log);
        var service = new MmrService(new MmrHistoryStore(Path.Combine(_steam.DataRoot, "mmr.json"), _log), packs, _log);
        string console = ConsoleLog.PathIn(_steam.GameRoot);

        Assert.Equal(0, service.Collect(_steam.Read()));

        File.WriteAllText(console, Reading(Account, 3450, null, null, "a", At) + Reading(Account, 3475, 25, 1, "b", At + 100));
        Assert.Equal(2, service.Collect(_steam.Read()));
        Assert.Equal(0, service.Collect(_steam.Read()));
        Assert.Equal(3475, MmrHistoryStore.Latest(service.History.Load()).Single().Mmr);
    }

    [Fact]
    public void Refresh_puts_the_newest_reading_into_a_pack_with_the_line()
    {
        var store = new InstallStateStore(Path.Combine(_steam.DataRoot, "install.json"), _log);
        var packs = new PackService(store, _log);
        var service = new MmrService(new MmrHistoryStore(Path.Combine(_steam.DataRoot, "mmr.json"), _log), packs, _log);
        byte[] pak = PackBuilder.Build(new PackMarker(PackBuilder.AppName, "0.1.0", "6967", DateTimeOffset.UnixEpoch), MmrMenu.PackFiles([], "ru"));

        // A pack without the line is left alone.
        packs.Install(_steam.Read, pak);
        Assert.False(service.RefreshPack(_steam.Read(), "ru"));

        packs.Install(_steam.Read, pak, mmr: true);
        File.WriteAllText(ConsoleLog.PathIn(_steam.GameRoot), Reading(Account, 3475, 25, 1, "b", At));
        service.Collect(_steam.Read());
        Assert.True(service.RefreshPack(_steam.Read(), "ru"));

        Assert.Contains("\"mmr\":3475", Encoding.UTF8.GetString(CompiledResource.Data(ReadFromPack(store.Load()!.PakPath, MmrMenu.DataPath))), StringComparison.Ordinal);
        Assert.Equal(PackHealth.Ready, packs.Check(_steam.Read()));
    }

    [Fact]
    public void Replacing_files_changes_only_them_and_keeps_the_pack_ours()
    {
        var store = new InstallStateStore(Path.Combine(_steam.DataRoot, "install.json"), _log);
        var packs = new PackService(store, _log);
        byte[] pak = PackBuilder.Build(
            new PackMarker(PackBuilder.AppName, "0.1.0", "6967", DateTimeOffset.UnixEpoch),
            new Dictionary<string, byte[]> { ["a/data.txt"] = [1], ["b/other.txt"] = [2] });
        InstallState installed = packs.Install(_steam.Read, pak, mmr: true).State!;

        Assert.True(packs.ReplaceFiles(_steam.Read(), new Dictionary<string, byte[]> { ["a/data.txt"] = [3, 4] }));

        InstallState state = store.Load()!;
        Assert.NotEqual(installed.Sha1, state.Sha1);
        Assert.Equal(installed with { Sha1 = state.Sha1 }, state);
        Assert.Equal([3, 4], ReadFromPack(state.PakPath, "a/data.txt"));
        Assert.Equal([2], ReadFromPack(state.PakPath, "b/other.txt"));
        Assert.NotNull(ReadFromPack(state.PakPath, LanguageFolders.MarkerPath));
        Assert.Equal(PackHealth.Ready, packs.Check(_steam.Read()));

        // The same contents again: nothing is written.
        DateTime written = File.GetLastWriteTimeUtc(state.PakPath);
        Thread.Sleep(20);
        Assert.True(packs.ReplaceFiles(_steam.Read(), new Dictionary<string, byte[]> { ["a/data.txt"] = [3, 4] }));
        Assert.Equal(written, File.GetLastWriteTimeUtc(state.PakPath));
        Assert.Equal(state, store.Load());
    }

    [Fact]
    public void Files_are_not_replaced_in_a_pack_that_is_not_ours_or_in_use()
    {
        var store = new InstallStateStore(Path.Combine(_steam.DataRoot, "install.json"), _log);
        var packs = new PackService(store, _log);
        var replacement = new Dictionary<string, byte[]> { ["a/data.txt"] = [3] };
        Assert.False(packs.ReplaceFiles(_steam.Read(), replacement));

        byte[] pak = PackBuilder.Build(new PackMarker(PackBuilder.AppName, "0.1.0", "6967", DateTimeOffset.UnixEpoch), new Dictionary<string, byte[]> { ["a/data.txt"] = [1] });
        InstallState state = packs.Install(_steam.Read, pak).State!;

        Assert.False(packs.ReplaceFiles(_steam.Read(), new Dictionary<string, byte[]> { ["c/absent.txt"] = [3] }));

        _steam.Running.Add("dota2");
        Assert.False(packs.ReplaceFiles(_steam.Read(), replacement));
        _steam.Running.Remove("dota2");

        File.WriteAllBytes(state.PakPath, [.. pak, 0]);
        Assert.False(packs.ReplaceFiles(_steam.Read(), replacement));
        Assert.Equal([.. pak, 0], File.ReadAllBytes(state.PakPath));
        Assert.Equal(state, store.Load());
    }

    [Fact]
    public void Pack_held_open_keeps_its_file_and_its_state()
    {
        var store = new InstallStateStore(Path.Combine(_steam.DataRoot, "install.json"), _log);
        var packs = new PackService(store, _log);
        byte[] pak = PackBuilder.Build(new PackMarker(PackBuilder.AppName, "0.1.0", "6967", DateTimeOffset.UnixEpoch), new Dictionary<string, byte[]> { ["a/data.txt"] = [1] });
        InstallState state = packs.Install(_steam.Read, pak).State!;

        using (File.Open(state.PakPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(packs.ReplaceFiles(_steam.Read(), new Dictionary<string, byte[]> { ["a/data.txt"] = [3] }));
        }

        Assert.Equal(pak, File.ReadAllBytes(state.PakPath));
        Assert.Equal(state, store.Load());
        Assert.False(File.Exists(state.PakPath + ".dotatune.tmp"));
        Assert.Equal(PackHealth.Ready, packs.Check(_steam.Read()));
    }

    [Fact]
    public void Unchanged_console_log_is_not_read_again()
    {
        var packs = new PackService(new InstallStateStore(Path.Combine(_steam.DataRoot, "install.json"), _log), _log);
        var history = new MmrHistoryStore(Path.Combine(_steam.DataRoot, "mmr.json"), _log);
        var service = new MmrService(history, packs, _log);
        string console = ConsoleLog.PathIn(_steam.GameRoot);
        File.WriteAllText(console, Reading(Account, 3450, null, null, "a", At));
        Assert.Equal(1, service.Collect(_steam.Read()));

        // The history lost the reading, the log did not change: nothing is read, so nothing comes back.
        File.Delete(Path.Combine(_steam.DataRoot, "mmr.json"));
        Assert.Equal(0, service.Collect(_steam.Read()));

        File.AppendAllText(console, Reading(Account, 3475, 25, 1, "b", At + 100));
        Assert.Equal(2, service.Collect(_steam.Read()));
    }

    [Theory]
    [InlineData("-novid", null, "-novid")]
    [InlineData("-novid", true, "-novid -condebug")]
    [InlineData("-novid -condebug", false, "-novid")]
    [InlineData("-novid -condebug", null, "-novid -condebug")]
    public void Console_log_option_follows_its_switch(string line, bool? consoleLog, string expected) =>
        Assert.Equal(expected, LaunchOptionsEditor.Plan(line, noVideo: true, noJoystick: false, removeDead: false, consoleLog));

    private static byte[] ReadFromPack(string pak, string path)
    {
        using var package = new Package();
        package.Read(pak);
        package.ReadEntry(package.FindEntry(path)!, out byte[] data);
        return data;
    }
}
