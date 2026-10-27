using System.Text;

namespace DotaTune.Core.Config;

/// <summary>
/// The DotaTune block inside autoexec.cfg. The text is handled as Latin-1, byte for byte, so
/// the person's own lines keep their encoding and line endings; the block itself is ASCII.
/// </summary>
public static class AutoexecText
{
    public const string BeginMarker = "// DotaTune begin: lines down to the end mark are written by DotaTune";
    public const string EndMarker = "// DotaTune end";

    public static Encoding Encoding => Encoding.Latin1;

    /// <summary>Start of the begin line and the end of the end line with its line break; null when there is no block.</summary>
    /// <exception cref="InvalidDataException">A mark is missing, out of order or doubled.</exception>
    public static (int Start, int End)? FindBlock(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var begins = new List<int>();
        var ends = new List<(int Start, int End)>();
        foreach ((int start, int end, string line) in SplitLines(text))
        {
            string trimmed = line.Trim();
            // Exact marks only, so an own comment like "// DotaTune end of binds" is not taken for one.
            if (trimmed.Equals("// DotaTune begin", StringComparison.Ordinal) || trimmed.StartsWith("// DotaTune begin:", StringComparison.Ordinal))
            {
                begins.Add(start);
            }
            else if (trimmed.Equals(EndMarker, StringComparison.Ordinal))
            {
                ends.Add((start, end));
            }
        }

        if (begins.Count == 0 && ends.Count == 0)
        {
            return null;
        }

        if (begins.Count != 1 || ends.Count != 1 || ends[0].Start < begins[0])
        {
            throw new InvalidDataException($"autoexec.cfg has {begins.Count} begin and {ends.Count} end marks of DotaTune");
        }

        return (begins[0], ends[0].End);
    }

    /// <summary>Command lines inside the block; null when there is no block.</summary>
    /// <exception cref="InvalidDataException">The marks are broken.</exception>
    public static IReadOnlyList<ConfigLine>? BlockLines(string text)
    {
        if (FindBlock(text) is not (int start, int end))
        {
            return null;
        }

        var lines = new List<ConfigLine>();
        foreach ((_, _, string line) in SplitLines(text[start..end]))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
            lines.Add(space < 0 ? new ConfigLine(trimmed, string.Empty) : new ConfigLine(trimmed[..space], trimmed[(space + 1)..].Trim()));
        }

        return lines;
    }

    /// <summary>The text with the block in place of an old one, or added at the end.</summary>
    /// <exception cref="InvalidDataException">The marks are broken.</exception>
    public static string WithBlock(string text, IReadOnlyList<ConfigLine> lines)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lines);
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var block = new StringBuilder();
        block.Append(BeginMarker).Append(newline);
        foreach (ConfigLine line in lines)
        {
            if (line.Name.Any(c => c is < '!' or > '~') || line.Value.Any(c => c is < ' ' or > '~' or '"' or ';'))
            {
                throw new ArgumentException($"Not a plain console line: {line}", nameof(lines));
            }

            block.Append(line.Name).Append(' ').Append(line.Value).Append(newline);
        }

        block.Append(EndMarker).Append(newline);
        if (FindBlock(text) is (int start, int end))
        {
            return string.Concat(text.AsSpan(0, start), block.ToString(), text.AsSpan(end));
        }

        return text.Length == 0 || text.EndsWith('\n') ? text + block : text + newline + block;
    }

    /// <param name="dropBreakBefore">The block was added after a last line without a line break, and that break goes too.</param>
    /// <exception cref="InvalidDataException">The marks are broken.</exception>
    public static string WithoutBlock(string text, bool dropBreakBefore)
    {
        if (FindBlock(text) is not (int start, int end))
        {
            return text;
        }

        if (dropBreakBefore && end == text.Length)
        {
            if (text.AsSpan(0, start).EndsWith("\r\n", StringComparison.Ordinal))
            {
                start -= 2;
            }
            else if (start > 0 && text[start - 1] == '\n')
            {
                start--;
            }
        }

        return string.Concat(text.AsSpan(0, start), text.AsSpan(end));
    }

    /// <summary>Lines with their start, their end after the line break, and their text without it.</summary>
    private static IEnumerable<(int Start, int End, string Line)> SplitLines(string text)
    {
        int start = 0;
        while (start < text.Length)
        {
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline + 1;
            yield return (start, end, text[start..end].TrimEnd('\r', '\n'));
            start = end;
        }
    }
}
