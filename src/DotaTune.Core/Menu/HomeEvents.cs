using DotaTune.Core.Effects;
using DotaTune.Core.Game;

namespace DotaTune.Core.Menu;

/// <summary>
/// The column of event tiles at the right of the home page (Dark Carnival and Quartero's Curios on
/// build 6969), which the menu script dotatune_events hides. The script finds the column by its id
/// in the live menu; this checks that the game's home page still has it. The Play button is not in
/// the column.
/// </summary>
public static class HomeEvents
{
    public const string HomePath = "panorama/layout/dashboard_page_home.vxml_c";
    public const string ColumnId = "FrontpageContents";

    /// <summary>Reads the layout of the home page from game\dota\pak01_dir.vpk. Only reads.</summary>
    /// <exception cref="FileNotFoundException">The game has no pak01_dir.vpk.</exception>
    /// <exception cref="InvalidDataException">The archive cannot be read or has no home page.</exception>
    public static byte[] ReadGameHome(string dotaFolder) => GameArchive.ReadFile(dotaFolder, HomePath);

    /// <summary>Null when a panel of the home page has the column's id; otherwise why not, for the log.</summary>
    public static string? Problem(ReadOnlySpan<byte> home)
    {
        Kv3Value root;
        try
        {
            root = PanoramaLayout.Tree(home);
        }
        catch (InvalidDataException ex)
        {
            return $"the home page layout is not readable: {ex.Message}";
        }

        bool found = PanoramaLayout.Nodes(root).Any(node =>
            node["eType"]?.Text == "PANEL_ATTRIBUTE" && node["name"]?.Text == "id" && node["child"]?["name"]?.Text == ColumnId);
        return found ? null : $"the home page has no panel {ColumnId}";
    }
}
