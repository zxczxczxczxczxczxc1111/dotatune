using System.Text;
using DotaTune.Core.Effects;
using DotaTune.Core.Menu;
using DotaTune.Core.Mmr;

namespace DotaTune.Tests;

public class HomeEventsTests
{
    [Fact]
    public void Home_page_with_the_column_takes_the_script()
    {
        Assert.Null(HomeEvents.Problem(MenuGameFiles.HomeTree()));

        Assert.Equal("the home page has no panel FrontpageContents", HomeEvents.Problem(MenuGameFiles.HomeTree(withColumn: false)));
        // A panel named after the column is not the column: only its id counts.
        Assert.NotNull(HomeEvents.Problem(MenuGameFiles.Layout(MenuGameFiles.Panel("FrontpageContents", [("class", "FrontpageContents")]))));
        Assert.StartsWith("the home page layout is not readable", HomeEvents.Problem(GameFiles.Resource()), StringComparison.Ordinal);
        Assert.StartsWith("the home page layout is not readable", HomeEvents.Problem([1, 2, 3]), StringComparison.Ordinal);
    }

    [Fact]
    public void Hidden_events_go_in_without_the_mmr_line()
    {
        Dictionary<string, byte[]> files = MmrMenu.PackFiles(null, "ru", hideEvents: true);

        Assert.Equal(MmrMenu.Paths.Order(StringComparer.Ordinal), files.Keys.Order(StringComparer.Ordinal));
        string events = Text(files[MmrMenu.EventsPath]);
        Assert.Contains("'" + HomeEvents.ColumnId + "'", events, StringComparison.Ordinal);
        Assert.Contains("column.visible = false", events, StringComparison.Ordinal);

        // The MMR script carries no code, and its data is the template's own.
        Assert.StartsWith("// DotaTune", Text(files[MmrMenu.ScriptPath]), StringComparison.Ordinal);
        Assert.DoesNotContain("$.", Text(files[MmrMenu.ScriptPath]), StringComparison.Ordinal);
        Assert.StartsWith("var DotaTuneMmrData = null;", Text(files[MmrMenu.DataPath]), StringComparison.Ordinal);
    }

    [Fact]
    public void Both_parts_go_in_together()
    {
        Dictionary<string, byte[]> files = MmrMenu.PackFiles([], "en", hideEvents: true);

        Assert.Contains(HomeEvents.ColumnId, Text(files[MmrMenu.EventsPath]), StringComparison.Ordinal);
        Assert.Contains("DOTATUNE_MMR", Text(files[MmrMenu.ScriptPath]), StringComparison.Ordinal);
        Assert.Contains("\"lang\":\"en\"", Text(files[MmrMenu.DataPath]), StringComparison.Ordinal);
    }

    private static string Text(byte[] script) => Encoding.UTF8.GetString(CompiledResource.Data(script));
}
