using System.Buffers.Binary;

namespace DotaTune.Core.Effects;

/// <summary>A game file the pack carries changed: its path inside the game and the new bytes.</summary>
public sealed record PackedFile(string Path, byte[] Bytes);

/// <summary>
/// The haze over the fog of war. The post-process material of the map modulates the fog with a
/// cloud texture; the pack carries that texture with every pixel at zero, as Minify's Dark Terrain
/// does. Checked on build 6967: the drifting haze goes, the darkening of unseen ground stays.
/// </summary>
public static class FogMist
{
    public const string Material = "materials/dev/deferred_post_process.vmat_c";

    private const int HeaderSize = 16;
    private const int BlockEntrySize = 12;
    private const byte Rgba8888 = 4;

    /// <summary>The cleared texture; null when the material names no mist texture or the texture is not plain RGBA.</summary>
    /// <param name="material">The compiled post-process material.</param>
    /// <param name="read">Reads a compiled file of the game by its path; null when there is none.</param>
    /// <exception cref="InvalidDataException">The material is not a readable resource.</exception>
    public static PackedFile? Clear(ReadOnlySpan<byte> material, Func<string, byte[]?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        string? texture = ResourceReferences.Read(material)
            .Select(ParticleGraph.SourceName)
            .FirstOrDefault(r => r.EndsWith(".vtex", StringComparison.Ordinal) && r[(r.LastIndexOf('/') + 1)..].Contains("mist", StringComparison.Ordinal));
        if (texture is null || read(texture + "_c") is not { } bytes)
        {
            return null;
        }

        byte[]? cleared = Cleared(bytes);
        return cleared is null ? null : new PackedFile(texture + "_c", cleared);
    }

    /// <summary>
    /// A copy with the pixels at zero, or null unless the texture is one uncompressed RGBA8888
    /// image without mipmaps stored right after the resource blocks: anything else would need a
    /// real encoder, and a wrong guess shows up as garbage over the whole map.
    /// </summary>
    internal static byte[]? Cleared(ReadOnlySpan<byte> texture)
    {
        if (texture.Length < HeaderSize)
        {
            return null;
        }

        // The first field is the size of the header and blocks; the pixels follow them.
        long blocksEnd = U32(texture, 0);
        long table = 8 + (long)U32(texture, 8);
        long count = U32(texture, 12);
        if (blocksEnd > texture.Length || table + (count * BlockEntrySize) > texture.Length)
        {
            return null;
        }

        for (long i = 0; i < count; i++)
        {
            int entry = (int)(table + (i * BlockEntrySize));
            if (!texture.Slice(entry, 4).SequenceEqual("DATA"u8))
            {
                continue;
            }

            long data = entry + 4 + (long)U32(texture, entry + 4);
            if (data + 28 > texture.Length)
            {
                return null;
            }

            // Version, flags, four floats of reflectivity, then width, height, depth, format, mip count.
            int at = (int)data;
            int width = BinaryPrimitives.ReadUInt16LittleEndian(texture[(at + 20)..]);
            int height = BinaryPrimitives.ReadUInt16LittleEndian(texture[(at + 22)..]);
            int depth = BinaryPrimitives.ReadUInt16LittleEndian(texture[(at + 24)..]);
            bool plain = texture[at + 26] == Rgba8888 && texture[at + 27] == 1 && depth == 1;
            if (!plain || width == 0 || height == 0 || texture.Length - blocksEnd != (long)width * height * 4)
            {
                return null;
            }

            byte[] copy = texture.ToArray();
            copy.AsSpan((int)blocksEnd).Clear();
            return copy;
        }

        return null;
    }

    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
}
