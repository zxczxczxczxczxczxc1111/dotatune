using System.Text;
using DotaTune.Core.Accept;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Effects;
using DotaTune.Core.Pack;

namespace DotaTune.Tests;

public sealed class AutoAcceptTests : IDisposable
{
    private readonly FakeSteam _steam = new();
    private readonly FileLog _log;

    public AutoAcceptTests() => _log = new FileLog(Path.Combine(_steam.DataRoot, "logs"));

    public void Dispose() => _steam.Dispose();

    /// <summary>Puts the window script into the game archive next to the usual files.</summary>
    internal static void AddGameScript(FakeSteam steam) =>
        GameFiles.WritePak(steam.Pak01, new Dictionary<string, byte[]>(GameFiles.Default()) { [AutoAccept.ScriptPath] = GameFiles.Script(GameFiles.AcceptScriptText) });

    internal static string Text(byte[] script) => Encoding.UTF8.GetString(CompiledResource.Data(script));

    [Fact]
    public void Lines_go_after_the_game_text_with_the_delay_and_the_rest_of_the_file_stays()
    {
        byte[] game = GameFiles.Script(GameFiles.AcceptScriptText);
        byte[] file = AutoAccept.PackFile(game, 7);

        string text = Text(file);
        Assert.StartsWith(GameFiles.AcceptScriptText + "\r\n\r\n// DotaTune auto accept", text, StringComparison.Ordinal);
        Assert.Contains("$.Schedule(7, function () {", text, StringComparison.Ordinal);
        Assert.Contains("$.Msg('DOTATUNE_ACCEPT after 7 s');", text, StringComparison.Ordinal);
        Assert.Contains("$.DispatchEvent('DOTAPlayAcceptMatch');", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{DELAY}", text, StringComparison.Ordinal);
        // Valve's text has CRLF, and so do the lines.
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // GameFiles.Script: the DATA size sits at 36 and the text starts at 44. Before the text only
        // the file size and the DATA size change.
        int dataAt = game.Length - Encoding.UTF8.GetByteCount(GameFiles.AcceptScriptText);
        Assert.Equal(44, dataAt);
        Assert.Equal(game[4..36], file[4..36]);
        Assert.Equal(game[40..dataAt], file[40..dataAt]);
        Assert.Equal(file.Length, BitConverter.ToInt32(file, 0));
        Assert.Equal(file.Length - dataAt, BitConverter.ToInt32(file, 36));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void Delay_outside_the_page_range_is_refused(int delay) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AutoAccept.PackFile(GameFiles.Script(GameFiles.AcceptScriptText), delay));

    [Theory]
    [InlineData(null, 5)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(15, 15)]
    [InlineData(99, 15)]
    public void Delay_from_the_settings_is_put_into_the_range(int? chosen, int expected) =>
        Assert.Equal(expected, AutoAccept.Delay(chosen));

    [Fact]
    public void Script_that_is_not_text_or_already_has_the_lines_is_not_taken()
    {
        byte[] game = GameFiles.Script(GameFiles.AcceptScriptText);
        Assert.Null(AutoAccept.Problem(game));
        Assert.Contains("already", AutoAccept.Problem(AutoAccept.PackFile(game, 5)), StringComparison.Ordinal);
        Assert.Contains("not script text", AutoAccept.Problem(GameFiles.Script("a\0b")), StringComparison.Ordinal);
        Assert.Contains("not UTF-8", AutoAccept.Problem(CompiledResource.WithData(game, [0xC3, 0x28])), StringComparison.Ordinal);
        Assert.Contains("not readable", AutoAccept.Problem([1, 2, 3]), StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => AutoAccept.PackFile(GameFiles.Script("a\0b"), 5));
    }

    [Fact]
    public void Script_is_read_from_the_game_archive()
    {
        Assert.Throws<InvalidDataException>(() => AutoAccept.ReadGameScript(_steam.Read().Dota!.DotaFolder));
        AddGameScript(_steam);
        Assert.Equal(GameFiles.Script(GameFiles.AcceptScriptText), AutoAccept.ReadGameScript(_steam.Read().Dota!.DotaFolder));
        Assert.Throws<FileNotFoundException>(() => AutoAccept.ReadGameScript(Path.Combine(_steam.DataRoot, "nowhere")));
    }

    [Fact]
    public void Replacing_files_writes_what_the_state_says_about_the_pack()
    {
        var store = new InstallStateStore(Path.Combine(_steam.DataRoot, "install.json"), _log);
        var packs = new PackService(store, _log);
        byte[] pak = PackBuilder.Build(
            new PackMarker(PackBuilder.AppName, "0.1.0", "6967", DateTimeOffset.UnixEpoch),
            new Dictionary<string, byte[]> { ["a/data.txt"] = [1] });
        InstallState installed = packs.Install(_steam.Read, pak, autoAccept: 5).State!;
        Assert.Equal(5, installed.AutoAccept);

        Assert.True(packs.ReplaceFiles(_steam.Read(), new Dictionary<string, byte[]> { ["a/data.txt"] = [2] }, s => s with { AutoAccept = 9 }));
        InstallState state = store.Load()!;
        Assert.Equal(installed with { Sha1 = state.Sha1, AutoAccept = 9 }, state);
        Assert.Equal(PackHealth.Ready, packs.Check(_steam.Read()));

        // The same contents with another description: only the state changes, and the hash cannot be described away.
        DateTime written = File.GetLastWriteTimeUtc(state.PakPath);
        Thread.Sleep(20);
        Assert.True(packs.ReplaceFiles(_steam.Read(), new Dictionary<string, byte[]> { ["a/data.txt"] = [2] }, s => s with { AutoAccept = 3, Sha1 = "0" }));
        Assert.Equal(written, File.GetLastWriteTimeUtc(state.PakPath));
        Assert.Equal(state with { AutoAccept = 3 }, store.Load());

        // The state cannot be written: false, as when the pack cannot be.
        string blocker = Path.Combine(_steam.DataRoot, "install.json.tmp");
        Directory.CreateDirectory(blocker);
        Assert.False(packs.ReplaceFiles(_steam.Read(), new Dictionary<string, byte[]> { ["a/data.txt"] = [2] }, s => s with { AutoAccept = 4 }));
        Assert.Equal(3, store.Load()!.AutoAccept);
        Directory.Delete(blocker);

        // A refused replace leaves the state as it was.
        _steam.Running.Add("dota2");
        Assert.False(packs.ReplaceFiles(_steam.Read(), new Dictionary<string, byte[]> { ["a/data.txt"] = [4] }, s => s with { AutoAccept = 1 }));
        Assert.Equal(3, store.Load()!.AutoAccept);
    }
}
