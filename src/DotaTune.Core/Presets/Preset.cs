using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DotaTune.Core.Config;
using DotaTune.Core.Effects;
using DotaTune.Core.Steam;
using ValveKeyValue;

namespace DotaTune.Core.Presets;

/// <summary>Files a preset may carry besides the choice; only in the .dotatune file.</summary>
public enum PresetPart
{
    HeroGrids,
    Hotkeys,
    GameSettings,
}

/// <summary>
/// What a preset sets in the program: names, not files, so the receiving program builds the
/// pack for its own game build. Names are strings: an older program shows what it does not know.
/// </summary>
public sealed record PresetChoice
{
    /// <summary>Effect groups the pack replaces; null is every group.</summary>
    public IReadOnlyList<string>? Effects { get; init; }

    public GameConfig? Config { get; init; }

    /// <summary>Launch options that are on, from <see cref="PresetReview.AllowedLaunch"/>.</summary>
    public IReadOnlyList<string>? Launch { get; init; }
}

/// <summary>manifest.json of a .dotatune file.</summary>
public sealed record PresetManifest(int FormatVersion, string AppVersion, string? GameBuild, DateTimeOffset CreatedAt, IReadOnlyList<string> Parts);

public enum PresetProblem
{
    NotAPreset,

    /// <summary>Made by a newer DotaTune with a format this one does not know.</summary>
    Newer,

    /// <summary>The code is cut or changed, or the file is broken.</summary>
    Damaged,
    TooLarge,
}

public sealed class PresetException(PresetProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    public PresetProblem Problem { get; } = problem;
}

/// <summary>A loaded preset; parts are already checked and written anew.</summary>
public sealed record PresetPackage(PresetManifest? Manifest, PresetChoice Choice, IReadOnlyDictionary<PresetPart, byte[]> Parts);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PresetChoice))]
[JsonSerializable(typeof(PresetManifest))]
internal sealed partial class PresetJsonContext : JsonSerializerContext;

/// <summary>
/// Short text code of a choice for a chat: "DT1-", then base64url of raw deflate of the
/// choice JSON with its CRC32 at the end. The digit after DT is the format version.
/// </summary>
public static partial class PresetCode
{
    public const int Version = 1;
    public const string Prefix = "DT1-";
    private const int MaxJsonBytes = 64 * 1024;
    private const int MaxCodeChars = 100_000;

    public static string Encode(PresetChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(choice, PresetJsonContext.Default.PresetChoice);
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(json);
        }

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32.Compute(json));
        packed.Write(crc);
        return Prefix + Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <exception cref="PresetException">Not a code, a newer format, cut or changed.</exception>
    public static PresetChoice Decode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length > MaxCodeChars)
        {
            throw new PresetException(PresetProblem.TooLarge, "The code is too long");
        }

        // Chats wrap long lines: spaces and line breaks inside the code are not part of it.
        string text = string.Concat(code.Where(c => !char.IsWhiteSpace(c)));
        Match match = CodeShape().Match(text);
        if (!match.Success || !int.TryParse(match.Groups[1].ValueSpan, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int version) || version < 1)
        {
            throw new PresetException(PresetProblem.NotAPreset, "Not a DotaTune code");
        }

        if (version > Version)
        {
            throw new PresetException(PresetProblem.Newer, $"Code format {version} is newer than {Version}");
        }

        string body = match.Groups[2].Value.Replace('-', '+').Replace('_', '/');
        byte[] data;
        try
        {
            data = Convert.FromBase64String(body.PadRight(body.Length + ((4 - (body.Length % 4)) % 4), '='));
        }
        catch (FormatException ex)
        {
            throw new PresetException(PresetProblem.Damaged, "The code is not base64url", ex);
        }

        if (data.Length < 5)
        {
            throw new PresetException(PresetProblem.Damaged, "The code is too short");
        }

        byte[] json;
        try
        {
            using var deflate = new DeflateStream(new MemoryStream(data, 0, data.Length - 4), CompressionMode.Decompress);
            json = Limits.ReadAll(deflate, MaxJsonBytes);
        }
        catch (InvalidDataException ex)
        {
            throw new PresetException(PresetProblem.Damaged, "The code does not unpack", ex);
        }

        if (Crc32.Compute(json) != BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4)))
        {
            throw new PresetException(PresetProblem.Damaged, "The code checksum does not match");
        }

        return Limits.Choice(json);
    }

    [GeneratedRegex("^DT([0-9]{1,4})-([A-Za-z0-9_-]+)$")]
    private static partial Regex CodeShape();
}

/// <summary>
/// A .dotatune file: a zip with manifest.json, choice.json and the parts under fixed names.
/// Reading takes only those names, caps every size and writes each part anew through its
/// parser; nothing from the file is copied into the game as it is.
/// </summary>
public static class PresetFile
{
    public const int FormatVersion = 1;
    public const string Extension = ".dotatune";
    public const long MaxFileBytes = 8 * 1024 * 1024;
    public const int MaxEntryBytes = 2 * 1024 * 1024;
    private const string ManifestName = "manifest.json";
    private const string ChoiceName = "choice.json";

    private static readonly Dictionary<PresetPart, string> PartNames = new()
    {
        [PresetPart.HeroGrids] = "hero_grid_config.json",
        [PresetPart.Hotkeys] = "dotakeys_personal.lst",
        [PresetPart.GameSettings] = "user_convars.vcfg",
    };

    private static readonly JsonSerializerOptions Indented = new(PresetJsonContext.Default.Options) { WriteIndented = true };

    public static void Write(string path, PresetManifest manifest, PresetChoice choice, IReadOnlyDictionary<PresetPart, byte[]> parts)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(parts);
        string temp = path + ".tmp";
        try
        {
            using (var zip = new ZipArchive(File.Create(temp), ZipArchiveMode.Create))
            {
                Add(ManifestName, JsonSerializer.SerializeToUtf8Bytes(manifest, Indented.GetTypeInfo(typeof(PresetManifest))));
                Add(ChoiceName, JsonSerializer.SerializeToUtf8Bytes(choice, Indented.GetTypeInfo(typeof(PresetChoice))));
                foreach ((PresetPart part, byte[] content) in parts)
                {
                    Add(PartNames[part], content);
                }

                void Add(string name, byte[] content)
                {
                    using Stream entry = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                    entry.Write(content);
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // A half-written file is not left next to the one the user chose.
            File.Delete(temp);
            throw;
        }
    }

    /// <exception cref="PresetException">Not a preset, a newer format, broken or too large.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static PresetPackage Read(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > MaxFileBytes)
        {
            throw new PresetException(PresetProblem.TooLarge, $"The file is larger than {MaxFileBytes} bytes");
        }

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new PresetException(PresetProblem.NotAPreset, "The file is not a zip archive", ex);
        }

        using (zip)
        {
            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName;
                if (name.Length == 0 || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Contains("..", StringComparison.Ordinal))
                {
                    throw new PresetException(PresetProblem.NotAPreset, $"Entry \"{name}\" is not a plain name");
                }

                if (name != ManifestName && name != ChoiceName && !PartNames.ContainsValue(name))
                {
                    continue;
                }

                if (entries.ContainsKey(name))
                {
                    throw new PresetException(PresetProblem.Damaged, $"Entry \"{name}\" is there twice");
                }

                try
                {
                    using Stream content = entry.Open();
                    entries[name] = Limits.ReadAll(content, MaxEntryBytes);
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                {
                    // NotSupported: a password or a compression method other than Deflate, not made by DotaTune.
                    throw new PresetException(PresetProblem.Damaged, $"Entry \"{name}\" does not unpack", ex);
                }
            }

            if (!entries.TryGetValue(ManifestName, out byte[]? manifestBytes))
            {
                throw new PresetException(PresetProblem.NotAPreset, "There is no manifest.json");
            }

            PresetManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize(manifestBytes, PresetJsonContext.Default.PresetManifest)
                    ?? throw new PresetException(PresetProblem.Damaged, "manifest.json is empty");
            }
            catch (JsonException ex)
            {
                throw new PresetException(PresetProblem.Damaged, "manifest.json is not valid", ex);
            }

            if (manifest.FormatVersion > FormatVersion)
            {
                throw new PresetException(PresetProblem.Newer, $"File format {manifest.FormatVersion} is newer than {FormatVersion}");
            }

            if (manifest.FormatVersion < 1)
            {
                throw new PresetException(PresetProblem.NotAPreset, "manifest.json has no format version");
            }

            PresetChoice choice = entries.TryGetValue(ChoiceName, out byte[]? choiceBytes) ? Limits.Choice(choiceBytes) : new PresetChoice();
            var parts = new Dictionary<PresetPart, byte[]>();
            foreach ((PresetPart part, string name) in PartNames)
            {
                if (entries.TryGetValue(name, out byte[]? content))
                {
                    parts[part] = Normalize(part, content);
                }
            }

            return new PresetPackage(manifest, choice, parts);
        }
    }

    /// <summary>The part written anew through its parser: JSON for grids, KeyValues for the rest.</summary>
    /// <exception cref="PresetException">The part is not what the game writes.</exception>
    public static byte[] Normalize(PresetPart part, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            return part == PresetPart.HeroGrids ? TransferFiles.NormalizeHeroGrids(content) : KeyValuesText(content);
        }
        catch (InvalidDataException ex)
        {
            throw new PresetException(PresetProblem.Damaged, $"{PartNames[part]} is not valid: {ex.Message}", ex);
        }
    }

    /// <summary>The files a preset's parts become in an account folder; grids are merged with the account's own.</summary>
    /// <exception cref="InvalidDataException">The account's own grid file is broken.</exception>
    public static IReadOnlyList<(string Path, byte[] Content)> TargetFiles(string targetFolder, IReadOnlyDictionary<PresetPart, byte[]> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var files = new List<(string, byte[])>();
        foreach ((PresetPart part, byte[] content) in parts)
        {
            switch (part)
            {
                case PresetPart.HeroGrids:
                    string grids = TransferFiles.Full(targetFolder, TransferFiles.HeroGridPath);
                    files.Add((TransferFiles.HeroGridPath, TransferFiles.MergeHeroGrids(content, File.Exists(grids) ? File.ReadAllBytes(grids) : null)));
                    break;
                case PresetPart.Hotkeys:
                    files.Add((TransferFiles.HotkeysPath, content));
                    break;
                case PresetPart.GameSettings:
                    files.AddRange(TransferFiles.GameSettingsCopies(content));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(parts), part, null);
            }
        }

        return files;
    }

    /// <summary>
    /// Game KeyValues text written anew. The game does not escape: a key "\" is a backslash
    /// binding, so escape sequences are off both ways.
    /// </summary>
    private static byte[] KeyValuesText(byte[] content)
    {
        var options = new KVSerializerOptions { HasEscapeSequences = false };
        KVSerializer serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
        try
        {
            KVDocument document = serializer.Deserialize(new MemoryStream(content, writable: false), options);
            using var output = new MemoryStream();
            serializer.Serialize(output, document, options);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is KeyValueException or FormatException or NotSupportedException or InvalidOperationException or EndOfStreamException)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }
}

/// <summary>What a preset changes here: known names apart from those this version does not know.</summary>
public sealed record PresetReview(
    IReadOnlyList<EffectGroup>? Effects,
    IReadOnlyList<string> UnknownEffects,
    GameConfig? Config,
    IReadOnlyList<string> Launch,
    IReadOnlyList<string> UnknownLaunch)
{
    /// <summary>The only launch options a preset may turn on.</summary>
    public static IReadOnlyList<string> AllowedLaunch { get; } = [LaunchOptionsEditor.NoVideo, LaunchOptionsEditor.NoJoystick];

    public static PresetReview Of(PresetChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        List<EffectGroup>? effects = null;
        var unknownEffects = new List<string>();
        if (choice.Effects is not null)
        {
            effects = [];
            foreach (string name in choice.Effects)
            {
                // Exact names only: TryParse alone also takes "3" and "Weather, Sprays".
                if (Enum.TryParse(name, ignoreCase: false, out EffectGroup group) && string.Equals(group.ToString(), name, StringComparison.Ordinal))
                {
                    effects.Add(group);
                }
                else
                {
                    unknownEffects.Add(name);
                }
            }
        }

        IReadOnlyList<string> launch = choice.Launch ?? [];
        return new PresetReview(
            effects?.Distinct().ToList(),
            unknownEffects,
            // Values on the page's lists only, so the preview shows what the page will get.
            choice.Config?.Sanitized().Snapped(),
            [.. AllowedLaunch.Where(a => launch.Contains(a, StringComparer.OrdinalIgnoreCase))],
            [.. launch.Where(l => !AllowedLaunch.Contains(l, StringComparer.OrdinalIgnoreCase))]);
    }

    /// <summary>The choice to export: settings as saved and the allowed options of the launch line.</summary>
    public static PresetChoice Choice(IReadOnlyList<EffectGroup>? effects, GameConfig? config, string? launchLine)
    {
        IReadOnlyList<LaunchOption> options = LaunchOptionsEditor.Parse(launchLine);
        return new PresetChoice
        {
            Effects = effects?.Select(e => e.ToString()).ToList(),
            Config = config,
            Launch = [.. AllowedLaunch.Where(a => LaunchOptionsEditor.Has(options, a))],
        };
    }
}

internal static class Limits
{
    /// <summary>Reads the stream to its end, refusing more than the limit whatever the header said.</summary>
    public static byte[] ReadAll(Stream stream, int limit)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new PresetException(PresetProblem.TooLarge, $"Unpacked data is larger than {limit} bytes");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public static PresetChoice Choice(byte[] json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, PresetJsonContext.Default.PresetChoice)
                ?? throw new PresetException(PresetProblem.Damaged, "The choice is empty");
        }
        catch (JsonException ex)
        {
            throw new PresetException(PresetProblem.Damaged, "The choice is not valid JSON", ex);
        }
    }
}

/// <summary>CRC-32 (IEEE, as in zip) of a block of bytes.</summary>
public static class Crc32
{
    private static readonly uint[] Table = Build();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
