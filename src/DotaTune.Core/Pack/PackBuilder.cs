using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotaTune.Core.Effects;
using ValvePak;

namespace DotaTune.Core.Pack;

/// <summary>What the marker inside every DotaTune pack says about it.</summary>
/// <param name="Effects">How many particles of each group the pack replaces with the empty one.</param>
/// <param name="MenuBackground">Id of the main menu background the pack puts in; null for the game's own.</param>
/// <param name="MouseColors">The mouse effects the pack recolours and their colours.</param>
/// <param name="Mmr">The pack puts the MMR line into the menu and the profile.</param>
public sealed record PackMarker(
    string App, string Version, string? GameBuild, DateTimeOffset Created, IReadOnlyDictionary<EffectGroup, int>? Effects = null,
    string? MenuBackground = null, MouseColorChoice? MouseColors = null, bool Mmr = false);

/// <summary>A pack ready to install and what is in it.</summary>
/// <param name="MouseColors">The mouse colours chosen when the pack was built.</param>
/// <param name="MouseNotTaken">Chosen mouse effects the pack could not recolour: the game has no such particles any more.</param>
/// <param name="Mmr">The MMR line went in.</param>
/// <param name="AutoAccept">Seconds after which the pack accepts a found match; null when it does not.</param>
public sealed record BuiltPack(
    byte[] Bytes, IReadOnlyDictionary<EffectGroup, int> Effects, string? MenuBackground = null,
    MouseColorChoice? MouseColors = null, IReadOnlyList<MouseEffect>? MouseNotTaken = null, bool Mmr = false, int? AutoAccept = null);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(PackMarker))]
[JsonSerializable(typeof(InstallState))]
internal sealed partial class PackJsonContext : JsonSerializerContext;

public static class PackBuilder
{
    public const string AppName = "DotaTune";

    /// <summary>
    /// Builds a single-file pakNN_dir.vpk in memory: the marker plus the given files.
    /// Paths inside are ASCII with forward slashes, the way the game names its files.
    /// </summary>
    public static byte[] Build(PackMarker marker, IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(files);
        using var package = new Package();
        package.AddFile(Game.LanguageFolders.MarkerPath, JsonSerializer.SerializeToUtf8Bytes(marker, PackJsonContext.Default.PackMarker), multiChunk: false);
        foreach ((string path, byte[] data) in files)
        {
            if (path.Any(c => c > 0x7F) || path.Contains('\\', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Pack paths must be ASCII with forward slashes: {path}", nameof(files));
            }

            package.AddFile(path, data, multiChunk: false);
        }

        using var stream = new MemoryStream();
        package.Write(stream);
        return stream.ToArray();
    }

    public static string Sha1(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA1.HashData(data));

    public static string Sha1OfFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream));
    }
}
