using System.Text;
using System.Text.Json;

namespace DotaTune.Core.HeroGrids;

/// <summary>
/// The tag DotaTune adds to the names of the layouts it puts into the game. A tag in the name
/// survives the game writing the file again, a field of our own would not. It is not the
/// " #DotaTune" of the transfer between accounts, so the two never take out each other's layouts.
/// </summary>
public static class GridMark
{
    public const string Suffix = " #DotaTune-Import";

    public static bool Has(string name) => name is not null && name.EndsWith(Suffix, StringComparison.Ordinal);

    /// <summary>The name without the tag, also when a file of another player carries it.</summary>
    public static string Strip(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        while (name.EndsWith(Suffix, StringComparison.Ordinal))
        {
            name = name[..^Suffix.Length];
        }

        return name;
    }
}

/// <summary>A layout of the file: its bytes, its name and, when it is a valid one, the layout.</summary>
public sealed record GridEntry(int Start, int End, string? Name, HeroGridLayout? Layout)
{
    public bool IsOurs => Name is not null && GridMark.Has(Name);
}

/// <summary>
/// hero_grid_config.json with the place of every layout in its bytes. Edits cut out and paste
/// in whole layouts and leave every other byte as it was, so the player's grids stay exactly
/// as the game wrote them, whatever the layout of the file.
/// </summary>
public sealed class GridFile
{
    private const string EmptyText = "{\n\t\"version\": 3,\n\t\"configs\":\n\t[\n\t]\n}";

    /// <summary>The file DotaTune creates when the account has none, in the game's layout; a new array each time.</summary>
    public static byte[] Empty => Encoding.UTF8.GetBytes(EmptyText);

    private readonly byte[] _bytes;
    private readonly int _open;
    private readonly int _close;

    private GridFile(byte[] bytes, int open, int close, IReadOnlyList<GridEntry> entries)
    {
        _bytes = bytes;
        _open = open;
        _close = close;
        Entries = entries;
    }

    /// <summary>Every element of the "configs" list in file order, also ones that are not layouts.</summary>
    public IReadOnlyList<GridEntry> Entries { get; }

    /// <exception cref="InvalidDataException">Not a JSON object with one "configs" list.</exception>
    public static GridFile Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        int bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        int open = -1;
        int close = -1;
        var ranges = new List<(int Start, int End)>();
        try
        {
            var reader = new Utf8JsonReader(bytes.AsSpan(bom), new JsonReaderOptions { MaxDepth = 64 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new InvalidDataException("hero_grid_config.json is not a JSON object");
            }

            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                bool configs = reader.ValueTextEquals("configs"u8);
                reader.Read();
                if (!configs)
                {
                    reader.Skip();
                    continue;
                }

                if (open >= 0)
                {
                    throw new InvalidDataException("hero_grid_config.json has two \"configs\" lists");
                }

                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    throw new InvalidDataException("\"configs\" of hero_grid_config.json is not a list");
                }

                open = bom + (int)reader.TokenStartIndex;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    int start = bom + (int)reader.TokenStartIndex;
                    reader.Skip();
                    ranges.Add((start, bom + (int)reader.BytesConsumed));
                }

                close = bom + (int)reader.TokenStartIndex;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"hero_grid_config.json is not valid JSON: {ex.Message}", ex);
        }

        if (open < 0)
        {
            throw new InvalidDataException("hero_grid_config.json has no \"configs\" list");
        }

        var entries = new List<GridEntry>(ranges.Count);
        foreach ((int start, int end) in ranges)
        {
            using JsonDocument element = JsonDocument.Parse(bytes.AsMemory(start, end - start));
            JsonElement root = element.RootElement;
            string? name = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("config_name", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            entries.Add(new GridEntry(start, end, name, HeroGridLayout.From(root)));
        }

        return new GridFile(bytes, open, close, entries);
    }

    /// <summary>
    /// The file with the given entries cut out and the layouts added after the last remaining one,
    /// tagged with <see cref="GridMark"/>. A cut takes the comma and the spaces in front of the
    /// layout (behind it for the first one), which is exactly what an addition put there, so adding
    /// and then cutting gives back the same bytes.
    /// </summary>
    public byte[] Edit(IReadOnlySet<GridEntry> remove, IReadOnlyList<HeroGridLayout> add)
    {
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(add);
        int count = Entries.Count;
        var cuts = new List<(int Start, int End)>();
        int firstKept = -1;
        int lastKept = -1;
        for (int i = 0; i < count; i++)
        {
            if (!remove.Contains(Entries[i]))
            {
                firstKept = firstKept < 0 ? i : firstKept;
                lastKept = i;
            }
        }

        if (firstKept < 0 && count > 0 && remove.Count > 0)
        {
            cuts.Add((_open + 1, Entries[count - 1].End));
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                if (!remove.Contains(Entries[i]))
                {
                    continue;
                }

                cuts.Add(i < firstKept ? (Entries[0].Start, Entries[firstKept].Start) : (Entries[i - 1].End, Entries[i].End));
            }
        }

        (string lead, string separator, string? indent) = Style();
        // A file with CRLF gets CRLF inside our layouts too.
        bool crlf = lead.Contains("\r\n", StringComparison.Ordinal);
        var inserted = new StringBuilder();
        foreach (HeroGridLayout layout in add)
        {
            string text = layout.Write(GridMark.Suffix, indent);
            inserted.Append(inserted.Length == 0 && lastKept < 0 ? lead : separator)
                .Append(crlf ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text);
        }

        // The addition goes behind the last kept layout; no cut ever covers that point.
        int at = lastKept < 0 ? _open + 1 : Entries[lastKept].End;
        byte[] paste = Encoding.UTF8.GetBytes(inserted.ToString());
        using var output = new MemoryStream(_bytes.Length + paste.Length);
        bool pasted = false;
        int position = 0;
        foreach ((int start, int end) in cuts.OrderBy(c => c.Start).Append((_bytes.Length, _bytes.Length)))
        {
            if (start > position)
            {
                if (!pasted && position <= at && at <= start)
                {
                    output.Write(_bytes, position, at - position);
                    output.Write(paste);
                    output.Write(_bytes, at, start - at);
                    pasted = true;
                }
                else
                {
                    output.Write(_bytes, position, start - position);
                }
            }
            else if (!pasted && position == at)
            {
                output.Write(paste);
                pasted = true;
            }

            position = Math.Max(position, end);
        }

        return output.ToArray();
    }

    /// <summary>
    /// What goes in front of the first layout and between layouts, copied from the file, and the
    /// indent for a layout written the game's way; null indent is a file on one line.
    /// </summary>
    private (string Lead, string Separator, string? Indent) Style()
    {
        string lead;
        string separator;
        if (Entries.Count >= 2)
        {
            lead = Between(_open + 1, Entries[0].Start);
            separator = Between(Entries[^2].End, Entries[^1].Start);
        }
        else if (Entries.Count == 1)
        {
            lead = Between(_open + 1, Entries[0].Start);
            separator = "," + lead;
        }
        else
        {
            // An empty list: one step deeper than the line of its closing bracket.
            string inside = Between(_open + 1, _close);
            int newline = inside.LastIndexOf('\n');
            lead = newline < 0 ? string.Empty : "\n" + inside[(newline + 1)..] + "\t";
            separator = "," + lead;
        }

        int last = lead.LastIndexOf('\n');
        return (lead, separator, last < 0 ? null : lead[(last + 1)..]);
    }

    private string Between(int from, int to) => Encoding.UTF8.GetString(_bytes, from, to - from);
}
