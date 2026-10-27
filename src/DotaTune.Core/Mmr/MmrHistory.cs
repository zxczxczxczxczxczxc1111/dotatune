using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DotaTune.Core.Diagnostics;

namespace DotaTune.Core.Mmr;

/// <summary>One look at the MMR in the profile: the number the game showed and how it moved since the look before.</summary>
/// <param name="Account">Steam account id, as in userdata\&lt;id&gt;.</param>
/// <param name="Delta">Change since the reading before; null when there was none.</param>
/// <param name="Games">Ranked games between the two readings; null when the match history did not tell.</param>
/// <param name="Top">The newest ranked game in the match history as the game listed it, so the next reading can count games.</param>
/// <param name="At">When it was read, Unix seconds.</param>
public sealed record MmrReading(uint Account, int Mmr, int? Delta, int? Games, string? Top, long At)
{
    public DateTimeOffset Time => DateTimeOffset.FromUnixTimeSeconds(At);
}

/// <summary>A reading as the menu script prints it, with the version of its format.</summary>
internal sealed record MmrLine(int V, uint Account, int Mmr, int? Delta, int? Games, string? Top, long At);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<MmrReading>))]
[JsonSerializable(typeof(MmrLine))]
internal sealed partial class MmrJsonContext : JsonSerializerContext;

/// <summary>
/// Readings in game/dota/console.log. The menu script prints each new one as
/// <c>10/27 07:30:12 [PanoramaScript] DOTATUNE_MMR {"v":1,"account":...}</c>; the game writes the
/// file only with -condebug and starts it anew at every launch.
/// </summary>
public static partial class MmrLog
{
    public const int Version = 1;
    private const int MaxMmr = 30_000;
    private const int MaxGames = 1_000;
    private const int MaxTop = 300;

    // 2017 and 2096: a time outside them is not a clock reading.
    private const long FirstTime = 1_500_000_000;
    private const long LastTime = 4_000_000_000;

    /// <summary>Every well-formed reading in the text, in the order printed; a damaged line is skipped.</summary>
    public static IReadOnlyList<MmrReading> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var readings = new List<MmrReading>();
        foreach (Match match in ReadingLine().Matches(text))
        {
            MmrLine? line;
            try
            {
                line = JsonSerializer.Deserialize(match.Groups["json"].Value, MmrJsonContext.Default.MmrLine);
            }
            catch (JsonException)
            {
                // A line cut off by a crash or written by another version: the next one may be whole.
                continue;
            }

            if (line is not null && IsValid(line))
            {
                readings.Add(new MmrReading(line.Account, line.Mmr, line.Delta, line.Games, line.Top, line.At));
            }
        }

        return readings;
    }

    private static bool IsValid(MmrLine line) =>
        line.V == Version
        && line.Account > 0
        && line.Mmr is > 0 and <= MaxMmr
        && line.Delta is null or (>= -MaxMmr and <= MaxMmr)
        && line.Games is null or (>= 0 and <= MaxGames)
        && (line.Top is null || line.Top.Length <= MaxTop)
        && line.At is > FirstTime and < LastTime;

    [GeneratedRegex(@"\[PanoramaScript\]\s+DOTATUNE_MMR\s+(?<json>\{[^\r\n]*\})[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ReadingLine();
}

/// <summary>mmr.json next to the settings: every reading DotaTune has seen, oldest first.</summary>
public sealed class MmrHistoryStore(string file, FileLog log)
{
    /// <summary>Readings kept per account; the oldest go first. A season of games a day fits many times.</summary>
    public const int KeptPerAccount = 2_000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // The start of the app, the page and a pack build take readings in from different threads.
    private readonly Lock _gate = new();

    /// <exception cref="InvalidDataException">The file is there but cannot be read; it is left as it is.</exception>
    public IReadOnlyList<MmrReading> Load()
    {
        lock (_gate)
        {
            return LoadLocked();
        }
    }

    private List<MmrReading> LoadLocked()
    {
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file, Utf8NoBom), MmrJsonContext.Default.ListMmrReading) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Error($"MMR history {file} is unreadable", ex);
            throw new InvalidDataException($"{file} is unreadable: {ex.Message}", ex);
        }
    }

    /// <summary>Adds the readings not kept yet and returns how many those were.</summary>
    /// <exception cref="InvalidDataException">The file is there but cannot be read: nothing is written over it.</exception>
    public int Add(IEnumerable<MmrReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        lock (_gate)
        {
            return AddLocked(readings);
        }
    }

    private int AddLocked(IEnumerable<MmrReading> readings)
    {
        List<MmrReading> kept = LoadLocked();
        var seen = new HashSet<(uint, long, int)>(kept.Select(Key));
        int added = 0;
        foreach (MmrReading reading in readings)
        {
            if (seen.Add(Key(reading)))
            {
                kept.Add(reading);
                added++;
            }
        }

        if (added == 0)
        {
            return 0;
        }

        List<MmrReading> trimmed = [.. kept
            .GroupBy(r => r.Account)
            .SelectMany(g => g.OrderBy(r => r.At).TakeLast(KeptPerAccount))
            .OrderBy(r => r.At)
            .ThenBy(r => r.Account)];
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(trimmed, MmrJsonContext.Default.ListMmrReading), Utf8NoBom);
        File.Move(temp, file, overwrite: true);
        return added;
    }

    /// <summary>The newest reading of each account.</summary>
    public static IReadOnlyList<MmrReading> Latest(IEnumerable<MmrReading> readings) =>
        [.. readings.GroupBy(r => r.Account).Select(g => g.MaxBy(r => r.At)!).OrderBy(r => r.Account)];

    private static (uint, long, int) Key(MmrReading r) => (r.Account, r.At, r.Mmr);
}
