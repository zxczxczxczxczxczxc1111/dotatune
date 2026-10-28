using DotaTune.Core.Diagnostics;
using DotaTune.Core.Platform;

namespace DotaTune.Tests;

// BringOtherForward is not called here: on a developer machine it would pull the real DotaTune forward.
public class SingleInstanceTests
{
    [Fact]
    public void Second_claim_of_the_name_is_not_the_first_until_the_first_copy_is_gone()
    {
        using var folder = new CyrillicTempFolder();
        var log = new FileLog(Path.Combine(folder.Path, "logs"));
        string name = UniqueName();

        SingleInstance first = SingleInstance.Claim(name, log);
        Assert.True(first.IsFirst);
        using (SingleInstance second = SingleInstance.Claim(name, log))
        {
            Assert.False(second.IsFirst);
        }

        // The second copy closing its handle does not free the name, the first one does.
        using (SingleInstance again = SingleInstance.Claim(name, log))
        {
            Assert.False(again.IsFirst);
        }

        first.Dispose();
        using SingleInstance next = SingleInstance.Claim(name, log);
        Assert.True(next.IsFirst);
    }

    [Fact]
    public void Name_taken_by_another_kind_of_object_lets_the_copy_run()
    {
        using var folder = new CyrillicTempFolder();
        var log = new FileLog(Path.Combine(folder.Path, "logs"));
        string name = UniqueName();
        using var other = new EventWaitHandle(false, EventResetMode.ManualReset, name);

        using SingleInstance claim = SingleInstance.Claim(name, log);
        Assert.True(claim.IsFirst);
        Assert.Contains("No guard against a second copy", File.ReadAllText(log.CurrentFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Name_belongs_to_the_program_folder()
    {
        string installed = SingleInstance.NameFor(@"C:\Users\Тестер\AppData\Local\DotaTune\current\");
        Assert.StartsWith(@"Local\DotaTune.Running.", installed, StringComparison.Ordinal);
        Assert.Equal(installed, SingleInstance.NameFor(@"c:\users\тестер\appdata\local\dotatune\current"));
        Assert.NotEqual(installed, SingleInstance.NameFor(@"D:\claude11\dotatune\dotatune\src\DotaTune\bin\Debug\net10.0-windows"));
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--veloapp-install", "0.3.0" }, true)]
    [InlineData(new[] { "--veloapp-updated", "0.3.1" }, true)]
    [InlineData(new[] { "--veloapp-obsolete", "0.3.0" }, true)]
    [InlineData(new[] { "--veloapp-uninstall", "0.3.0" }, true)]
    [InlineData(new[] { "--debug" }, false)]
    public void Velopack_hooks_skip_the_guard(string[] args, bool hook) =>
        Assert.Equal(hook, Program.IsVelopackHook(args));

    private static string UniqueName() => $@"Local\DotaTune.Tests.{Guid.NewGuid():N}";
}
