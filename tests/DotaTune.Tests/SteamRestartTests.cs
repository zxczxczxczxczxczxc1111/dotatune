using System.ComponentModel;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Game;

namespace DotaTune.Tests;

public class SteamRestartTests
{
    private static readonly SteamRestartTimings Fast = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100));

    [Fact]
    public void Steam_is_closed_for_the_change_and_started_again()
    {
        using var steam = new FakeSteam();
        steam.Running.Add("steam");
        var client = new FakeSteamClient(steam);
        bool closedDuringChange = false;

        SteamRestartResult<string> result = Restart(steam, client).Around(steam.Read(), () =>
        {
            closedDuringChange = !steam.Running.Contains("steam");
            return "done";
        });

        Assert.Null(result.Problem);
        Assert.Equal("done", result.Result);
        Assert.True(closedDuringChange);
        Assert.Equal(["-shutdown", ""], client.Starts.Select(s => string.Join(' ', s.Skip(1))));
        Assert.All(client.Starts, s => Assert.Equal(Path.Combine(steam.SteamRoot, "steam.exe"), s[0]));
        Assert.Contains("steam", steam.Running);
    }

    [Fact]
    public void Web_helpers_are_waited_for_a_while_and_do_not_stop_the_change()
    {
        using var steam = new FakeSteam();
        steam.Running.UnionWith(["steam", "steamwebhelper"]);
        var client = new FakeSteamClient(steam);
        var log = new FileLog(Path.Combine(steam.DataRoot, "logs"));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        SteamRestartResult<string> result = Restart(steam, client, log).Around(steam.Read(), () => "done");

        Assert.Null(result.Problem);
        Assert.Equal("done", result.Result);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(100), "the helpers were not waited for");
        Assert.Contains("Steam web helpers still run", File.ReadAllText(log.CurrentFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Web_helpers_that_close_while_waited_for_are_logged()
    {
        using var steam = new FakeSteam();
        steam.Running.Add("steam");
        var client = new FakeSteamClient(steam);
        var log = new FileLog(Path.Combine(steam.DataRoot, "logs"));
        int helperChecks = 0;
        var probe = new ProbeOf(name => name == "steamwebhelper" ? ++helperChecks <= 3 : steam.IsRunning(name));

        SteamRestartResult<string> result = new SteamRestart(client, probe, log, Fast).Around(steam.Read(), () => "done");

        Assert.Null(result.Problem);
        Assert.Contains("Steam web helpers closed", File.ReadAllText(log.CurrentFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Running_dota_is_not_ended_by_closing_steam()
    {
        using var steam = new FakeSteam();
        steam.Running.UnionWith(["steam", "dota2"]);
        var client = new FakeSteamClient(steam);

        SteamRestartResult<string> result = Restart(steam, client).Around(steam.Read(), () => "done");

        Assert.Equal(SteamRestartProblem.DotaRunning, result.Problem);
        Assert.Empty(client.Starts);
    }

    [Fact]
    public void Steam_that_does_not_close_leaves_everything_as_it_was()
    {
        using var steam = new FakeSteam();
        steam.Running.Add("steam");
        var client = new FakeSteamClient(steam) { Closes = false };
        bool changed = false;

        SteamRestartResult<string> result = Restart(steam, client).Around(steam.Read(), () =>
        {
            changed = true;
            return "done";
        });

        Assert.Equal(SteamRestartProblem.DidNotClose, result.Problem);
        Assert.False(changed);
        Assert.Equal(["-shutdown"], client.Starts.Select(s => string.Join(' ', s.Skip(1))));
    }

    [Fact]
    public void Steam_is_started_again_when_the_change_fails()
    {
        using var steam = new FakeSteam();
        steam.Running.Add("steam");
        var client = new FakeSteamClient(steam);

        Assert.Throws<IOException>(() => Restart(steam, client).Around<string>(steam.Read(), () => throw new IOException("busy")));

        Assert.Equal(2, client.Starts.Count);
        Assert.Contains("steam", steam.Running);
    }

    [Fact]
    public void Steam_that_cannot_be_started_again_is_reported_with_the_result()
    {
        using var steam = new FakeSteam();
        steam.Running.Add("steam");
        var client = new FakeSteamClient(steam) { CanStart = false };

        SteamRestartResult<string> result = Restart(steam, client).Around(steam.Read(), () => "done");

        Assert.Equal(SteamRestartProblem.DidNotStart, result.Problem);
        Assert.Equal("done", result.Result);
    }

    [Fact]
    public void No_steam_closes_nothing()
    {
        using var steam = new FakeSteam();
        var client = new FakeSteamClient(steam);
        GameState game = steam.Read() with { Steam = null };

        SteamRestartResult<string> result = Restart(steam, client).Around(game, () => "done");

        Assert.Equal(SteamRestartProblem.NoSteam, result.Problem);
        Assert.Empty(client.Starts);
    }

    private sealed class ProbeOf(Func<string, bool> isRunning) : IProcessProbe
    {
        public bool IsRunning(string processName) => isRunning(processName);
    }

    internal static SteamRestart Restart(FakeSteam steam, FakeSteamClient client, FileLog? log = null) =>
        new(client, steam, log ?? new FileLog(Path.Combine(steam.DataRoot, "logs")), Fast);
}

/// <summary>
/// Stands in for steam.exe: -shutdown closes the fake Steam and, as the real one, clears the signed-in
/// account in the registry; a start without options opens it again.
/// </summary>
internal sealed class FakeSteamClient(FakeSteam steam) : IGameStarter
{
    public List<string[]> Starts { get; } = [];

    /// <summary>The account the started Steam puts in the registry; 0 as after the restart of 27.10.2026.</summary>
    public uint ActiveUserAfterStart { get; init; } = FakeSteam.AccountId;

    /// <summary>Whether -shutdown closes Steam.</summary>
    public bool Closes { get; init; } = true;

    /// <summary>Whether steam.exe can be started again.</summary>
    public bool CanStart { get; init; } = true;

    public void Start(string steamExe, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 && !CanStart)
        {
            throw new Win32Exception(2, "The system cannot find the file specified");
        }

        Starts.Add([steamExe, .. arguments]);
        if (arguments.Contains("-shutdown"))
        {
            if (Closes)
            {
                steam.Running.Remove("steam");
                steam.ActiveUser = 0;
            }
        }
        else
        {
            steam.Running.Add("steam");
            steam.ActiveUser = ActiveUserAfterStart;
        }
    }
}
