using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotaTune.Tests;

/// <summary>
/// Every place that starts an external program must be listed here together with how
/// its output is handled: console programs answer in the OEM code page and in the
/// language of Windows, so their text is never searched for words. The search walks
/// the syntax tree; a plain text search would also trip over comments.
/// </summary>
public class ProcessGuardTests
{
    /// <summary>"relative/path.cs: member" to what is done with the program's output.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["src/DotaTune.Core/Game/GameLauncher.cs: Start"] =
            "steam.exe -applaunch: output is not captured, the result is read from game/dota/console.log",
        ["src/DotaTune.Setup/SetupSteps.cs: InstallAsync"] =
            "Velopack setup.exe --silent: output is not captured, the result is the exit code, details go to its own log file",
        ["src/DotaTune.Setup/SetupSteps.cs: OpenApp"] =
            "DotaTune.exe from the install folder: a window program, nothing is read from it",
        ["src/DotaTune.Setup/SetupSteps.cs: OpenLogs"] =
            "the log folder opened through the shell: nothing is read",
    };

    [Fact]
    public void Every_process_start_is_listed()
    {
        List<string> found = FindProcessStarts();

        Assert.DoesNotContain(found, f => !Allowed.ContainsKey(f));
        Assert.DoesNotContain(Allowed.Keys, k => !found.Contains(k, StringComparer.Ordinal));
    }

    [Fact]
    public void The_guard_sees_starts_in_code_and_not_in_comments()
    {
        const string sample = """
            // Process.Start("cmd.exe") in a comment does not count
            class C
            {
                void A() => System.Diagnostics.Process.Start("x.exe");
                void B() { var psi = new ProcessStartInfo("y.exe"); }
                string D() => "Process.Start";
            }
            """;

        List<string> found = FindIn(CSharpSyntaxTree.ParseText(sample, cancellationToken: TestContext.Current.CancellationToken), "sample.cs");

        Assert.Equal(["sample.cs: A", "sample.cs: B"], found);
    }

    private static List<string> FindProcessStarts()
    {
        string src = Path.Combine(RepoPaths.Root, "src");
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(RepoPaths.Root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            found.AddRange(FindIn(CSharpSyntaxTree.ParseText(File.ReadAllText(file)), relative));
        }

        return found;
    }

    private static List<string> FindIn(SyntaxTree tree, string name)
    {
        var found = new List<string>();
        foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
        {
            bool starts = node switch
            {
                InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } =>
                    access.Name.Identifier.ValueText == "Start" && access.Expression.ToString().EndsWith("Process", StringComparison.Ordinal),
                BaseObjectCreationExpressionSyntax creation =>
                    creation is ObjectCreationExpressionSyntax { Type: var type } && type.ToString().EndsWith("ProcessStartInfo", StringComparison.Ordinal),
                _ => false,
            };
            if (starts)
            {
                string member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() switch
                {
                    MethodDeclarationSyntax m => m.Identifier.ValueText,
                    PropertyDeclarationSyntax p => p.Identifier.ValueText,
                    ConstructorDeclarationSyntax c => c.Identifier.ValueText,
                    _ => "?",
                };
                found.Add($"{name}: {member}");
            }
        }

        return found;
    }
}
