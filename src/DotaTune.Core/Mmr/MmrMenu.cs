using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotaTune.Core.Effects;
using DotaTune.Core.Game;

namespace DotaTune.Core.Mmr;

/// <summary>The last reading of one account as the menu script takes it.</summary>
internal sealed record MmrScriptReading(int Mmr, int? Delta, int? Games, string? Top, long At);

/// <summary>What the menu script finds in DotaTuneMmrData.</summary>
internal sealed record MmrScriptData(int V, string Lang, Dictionary<string, MmrScriptReading> Accounts);

// The default encoder writes every non-ASCII character as \uXXXX, so the script stays ASCII.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MmrScriptData))]
internal sealed partial class MmrScriptJsonContext : JsonSerializerContext;

/// <summary>
/// The files that put the MMR line into the game: the root layout of the menu with two scripts
/// added, the script itself and its data. The first two are compiled from Mmr\Panorama by
/// build\panorama.ps1; the data is made here from the compiled template by putting the text in
/// place of its DATA block, which holds nothing but the script text (checked on build 6967).
/// </summary>
public static class MmrMenu
{
    public const string BasePath = "panorama/layout/base.vxml_c";
    public const string ScriptPath = "panorama/scripts/dotatune_mmr.vjs_c";
    public const string DataPath = "panorama/scripts/dotatune_mmr_data.vjs_c";

    private const string ResourcePrefix = "DotaTune.Mmr.";
    private const string AstMember = "m_AST";
    private const string RootMember = "m_pRoot";
    private const string LineMember = "sourceLineColumn";

    /// <summary>Paths of the files in the pack.</summary>
    public static IReadOnlyList<string> Paths { get; } = [BasePath, ScriptPath, DataPath];

    /// <summary>Reads the root layout of the menu from game\dota\pak01_dir.vpk. Only reads.</summary>
    /// <exception cref="FileNotFoundException">The game has no pak01_dir.vpk.</exception>
    /// <exception cref="InvalidDataException">The archive cannot be read or has no root layout.</exception>
    public static byte[] ReadGameBase(string dotaFolder) => GameArchive.ReadFile(dotaFolder, BasePath);

    /// <summary>
    /// Null when the game's root layout is the one ours is made from: the same tree of panels,
    /// styles and attributes, line numbers aside, once our scripts are left out of ours.
    /// Otherwise what differs, for the log; the pack then leaves the game's layout alone.
    /// </summary>
    public static string? Problem(ReadOnlySpan<byte> gameBase)
    {
        string game;
        try
        {
            game = Shape(gameBase, withoutScripts: false);
        }
        catch (InvalidDataException ex)
        {
            return $"the game's root layout is not readable: {ex.Message}";
        }

        return string.Equals(game, Shape(Resource("base.vxml_c"), withoutScripts: true), StringComparison.Ordinal)
            ? null
            : $"the game's root layout is not the one DotaTune knows: {game[..Math.Min(game.Length, 400)]}";
    }

    /// <summary>The three files for the pack, with the newest reading of each account in the data.</summary>
    /// <param name="language">"ru" or "en": the words of the line in the menu.</param>
    public static Dictionary<string, byte[]> PackFiles(IEnumerable<MmrReading> latest, string language) => new(StringComparer.Ordinal)
    {
        [BasePath] = Resource("base.vxml_c"),
        [ScriptPath] = Resource("dotatune_mmr.vjs_c"),
        [DataPath] = DataFile(latest, language),
    };

    /// <summary>The compiled data template with <c>var DotaTuneMmrData = {...};</c> as its text.</summary>
    public static byte[] DataFile(IEnumerable<MmrReading> latest, string language)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(language);
        var accounts = new Dictionary<string, MmrScriptReading>(StringComparer.Ordinal);
        foreach (MmrReading r in latest)
        {
            accounts[r.Account.ToString(CultureInfo.InvariantCulture)] = new MmrScriptReading(r.Mmr, r.Delta, r.Games, r.Top, r.At);
        }

        string json = JsonSerializer.Serialize(new MmrScriptData(MmrLog.Version, language, accounts), MmrScriptJsonContext.Default.MmrScriptData);
        return CompiledResource.WithData(Resource("dotatune_mmr_data.vjs_c"), Encoding.UTF8.GetBytes($"var DotaTuneMmrData = {json};\n"));
    }

    /// <summary>The tree of the layout (block LaCo, binary KV3) as text, without line numbers.</summary>
    /// <exception cref="InvalidDataException">The file is not a compiled layout with a readable tree.</exception>
    private static string Shape(ReadOnlySpan<byte> layout, bool withoutScripts)
    {
        byte[] block = CompiledResource.Block(layout, "LaCo"u8) ?? throw new InvalidDataException("no LaCo block");
        Kv3Value root = Kv3Binary.Read(block).Root[AstMember]?[RootMember] ?? throw new InvalidDataException("no layout tree");
        var text = new StringBuilder();
        Write(root, text, withoutScripts);
        return text.ToString();
    }

    private static void Write(Kv3Value value, StringBuilder text, bool withoutScripts)
    {
        if (value.Members is { } members)
        {
            text.Append('{');
            foreach (Kv3Member member in members.Where(m => m.Name != LineMember))
            {
                text.Append(member.Name).Append('=');
                Write(member.Value, text, withoutScripts);
                text.Append(';');
            }

            text.Append('}');
        }
        else if (value.Items is { } items)
        {
            text.Append('[');
            foreach (Kv3Value item in items.Where(i => !(withoutScripts && i["eType"]?.Text == "SCRIPTS")))
            {
                Write(item, text, withoutScripts);
                text.Append(',');
            }

            text.Append(']');
        }
        else
        {
            text.Append(value.Text ?? (value.IsNumber ? value.Number.ToString(CultureInfo.InvariantCulture) : value.Type.ToString()));
        }
    }

    private static byte[] Resource(string name)
    {
        using Stream stream = typeof(MmrMenu).Assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidOperationException($"{ResourcePrefix}{name} is not built into DotaTune.Core");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
