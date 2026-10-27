using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DotaTune.Core.HeroGrids;

/// <summary>One box of heroes on the grid; positions and sizes in the game's units.</summary>
public sealed record HeroGridCategory(string Name, double X, double Y, double Width, double Height, IReadOnlyList<int> HeroIds);

/// <summary>
/// One layout of the hero grid editor ("config" in hero_grid_config.json), with the name it
/// has without the DotaTune mark. Read from files of other programs and the internet, so
/// everything in it is checked; written the way the game writes it.
/// </summary>
public sealed class HeroGridLayout
{
    // Art grids draw pictures with text: up to about 5000 one-symbol categories or lines of 200 symbols.
    private const int MaxName = 1024;
    private const int MaxCategories = 5000;
    private const int MaxHeroes = 512;
    private const double MaxCoordinate = 100_000;

    public HeroGridLayout(string name, IReadOnlyList<HeroGridCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(categories);
        Name = name;
        Categories = categories;
        Compact = Write(string.Empty, indent: null);
    }

    public string Name { get; }

    public IReadOnlyList<HeroGridCategory> Categories { get; }

    /// <summary>The layout as one line of JSON with numbers as the game keeps them; equal text, equal layout.</summary>
    public string Compact { get; }

    public bool SameAs(HeroGridLayout other) => other is not null && string.Equals(Compact, other.Compact, StringComparison.Ordinal);

    /// <summary>
    /// The layout in a config object; null when it is not one the game would show: no name, a
    /// control character in a name, a number out of range, a hero id that is not a positive integer.
    /// </summary>
    public static HeroGridLayout? From(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object
            || Text(config, "config_name") is not { Length: > 0 } name
            || !config.TryGetProperty("categories", out JsonElement list)
            || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() > MaxCategories)
        {
            return null;
        }

        var categories = new List<HeroGridCategory>();
        foreach (JsonElement item in list.EnumerateArray())
        {
            if (Category(item) is not { } category)
            {
                return null;
            }

            categories.Add(category);
        }

        string own = GridMark.Strip(name);
        return own.Length == 0 ? null : new HeroGridLayout(own, categories);
    }

    /// <summary>
    /// The config object as the game writes it: tabs, nested lists on their own line, numbers
    /// with six decimals. <paramref name="indent"/> is the indent of the object's own line, the
    /// first brace itself is not indented; null writes one compact line.
    /// </summary>
    public string Write(string mark, string? indent)
    {
        ArgumentNullException.ThrowIfNull(mark);
        var text = new StringBuilder();
        if (indent is null)
        {
            text.Append("{\"config_name\":").Append(Quote(Name + mark)).Append(",\"categories\":[");
            for (int i = 0; i < Categories.Count; i++)
            {
                HeroGridCategory c = Categories[i];
                text.Append(i == 0 ? "{" : ",{")
                    .Append("\"category_name\":").Append(Quote(c.Name))
                    .Append(",\"x_position\":").Append(Number(c.X))
                    .Append(",\"y_position\":").Append(Number(c.Y))
                    .Append(",\"width\":").Append(Number(c.Width))
                    .Append(",\"height\":").Append(Number(c.Height))
                    .Append(",\"hero_ids\":[").AppendJoin(',', c.HeroIds.Select(id => id.ToString(CultureInfo.InvariantCulture)))
                    .Append("]}");
            }

            return text.Append("]}").ToString();
        }

        string i1 = indent + "\t";
        string i2 = i1 + "\t";
        string i3 = i2 + "\t";
        text.Append("{\n")
            .Append(i1).Append("\"config_name\": ").Append(Quote(Name + mark)).Append(",\n")
            .Append(i1).Append("\"categories\":\n")
            .Append(i1).Append("[\n");
        for (int i = 0; i < Categories.Count; i++)
        {
            HeroGridCategory c = Categories[i];
            text.Append(i2).Append("{\n")
                .Append(i3).Append("\"category_name\": ").Append(Quote(c.Name)).Append(",\n")
                .Append(i3).Append("\"x_position\": ").Append(Number(c.X)).Append(",\n")
                .Append(i3).Append("\"y_position\": ").Append(Number(c.Y)).Append(",\n")
                .Append(i3).Append("\"width\": ").Append(Number(c.Width)).Append(",\n")
                .Append(i3).Append("\"height\": ").Append(Number(c.Height)).Append(",\n")
                .Append(i3).Append("\"hero_ids\":\n")
                .Append(i3).Append("[\n");
            for (int h = 0; h < c.HeroIds.Count; h++)
            {
                text.Append(i3).Append('\t').Append(c.HeroIds[h].ToString(CultureInfo.InvariantCulture))
                    .Append(h < c.HeroIds.Count - 1 ? ",\n" : "\n");
            }

            text.Append(i3).Append("]\n")
                .Append(i2).Append(i < Categories.Count - 1 ? "},\n" : "}\n");
        }

        return text.Append(i1).Append("]\n").Append(indent).Append('}').ToString();
    }

    private static HeroGridCategory? Category(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || Text(item, "category_name") is not { } name
            || Coordinate(item, "x_position") is not double x
            || Coordinate(item, "y_position") is not double y
            || Coordinate(item, "width") is not double width
            || width < 0
            || Coordinate(item, "height") is not double height
            || height < 0
            || !item.TryGetProperty("hero_ids", out JsonElement ids)
            || ids.ValueKind != JsonValueKind.Array
            || ids.GetArrayLength() > MaxHeroes)
        {
            return null;
        }

        var heroes = new List<int>();
        foreach (JsonElement id in ids.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int hero) || hero <= 0)
            {
                return null;
            }

            heroes.Add(hero);
        }

        return new HeroGridCategory(name, x, y, width, height, heroes);
    }

    /// <summary>A string property without control characters; an empty string is allowed.</summary>
    private static string? Text(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string text = value.GetString()!;
        return text.Length <= MaxName && !text.Any(char.IsControl) && !HasLoneSurrogate(text) ? text : null;
    }

    private static double? Coordinate(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double number)
        && double.IsFinite(number)
        && Math.Abs(number) <= MaxCoordinate
            ? number
            : null;

    private static bool HasLoneSurrogate(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The game keeps positions as 32-bit floats and prints them with six decimals.</summary>
    private static string Number(double value) => ((double)(float)value).ToString("F6", CultureInfo.InvariantCulture);

    /// <summary>A JSON string with non-ASCII letters as they are, as the game writes Cyrillic names.</summary>
    private static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
