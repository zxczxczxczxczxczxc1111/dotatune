using System.Text;
using System.Text.RegularExpressions;

namespace DotaTune.Updates;

public enum NoteKind
{
    /// <summary>"Добавлено", "Изменено", "Исправлено".</summary>
    Heading,

    /// <summary>One point of the list.</summary>
    Item,

    /// <summary>A line outside the list, as "Первый выпуск.".</summary>
    Text,
}

public sealed record NoteLine(NoteKind Kind, string Text)
{
    public bool IsHeading => Kind == NoteKind.Heading;

    public bool IsItem => Kind == NoteKind.Item;

    public bool IsText => Kind == NoteKind.Text;
}

/// <summary>
/// The section of a version in CHANGELOG.md, the same text as the release on GitHub. The file is
/// built into the program, so what is new shows after the restart without going online.
/// </summary>
internal static partial class ReleaseNotes
{
    public const string ResourceName = "CHANGELOG.md";

    /// <summary>Empty when the built-in file has no section of this version, as in a build between releases.</summary>
    public static IReadOnlyList<NoteLine> For(string version)
    {
        using Stream? stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return [];
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd(), version);
    }

    /// <summary>
    /// Headings, points and plain lines of the "## [version]" section up to the next one. A point
    /// takes its wrapped lines; code marks, bold and links are left as plain text.
    /// </summary>
    internal static IReadOnlyList<NoteLine> Parse(string changelog, string version)
    {
        ArgumentNullException.ThrowIfNull(changelog);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        string header = $"## [{version}]";
        var lines = new List<NoteLine>();
        bool inside = false;
        NoteKind kind = NoteKind.Text;
        StringBuilder? open = null;

        foreach (string raw in changelog.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inside)
                {
                    break;
                }

                inside = line.StartsWith(header, StringComparison.Ordinal);
                continue;
            }

            if (!inside)
            {
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Close();
                lines.Add(new NoteLine(NoteKind.Heading, Clean(line[4..])));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                Close();
                (kind, open) = (NoteKind.Item, new StringBuilder(line[2..].Trim()));
            }
            else if (line.Length == 0)
            {
                Close();
            }
            else if (open is not null)
            {
                open.Append(' ').Append(line);
            }
            else
            {
                (kind, open) = (NoteKind.Text, new StringBuilder(line));
            }
        }

        Close();
        return lines;

        void Close()
        {
            if (open is { Length: > 0 })
            {
                lines.Add(new NoteLine(kind, Clean(open.ToString())));
            }

            open = null;
        }
    }

    private static string Clean(string text) =>
        Link().Replace(text, "$1").Replace("`", string.Empty, StringComparison.Ordinal).Replace("**", string.Empty, StringComparison.Ordinal).Trim();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();
}
