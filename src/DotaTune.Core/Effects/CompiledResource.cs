using System.Buffers.Binary;

namespace DotaTune.Core.Effects;

/// <summary>
/// The blocks of a compiled Source 2 resource. A particle or a panorama script keeps DATA last,
/// after the references and the edit info, so a new block replaces the tail of the file and the
/// other blocks stay byte for byte.
/// </summary>
public static class CompiledResource
{
    private const int HeaderSize = 16;
    private const int BlockEntrySize = 12;

    /// <summary>The bytes of the DATA block.</summary>
    /// <exception cref="InvalidDataException">Not a resource, or it has no DATA block at its end.</exception>
    public static ReadOnlySpan<byte> Data(ReadOnlySpan<byte> resource) => resource.Slice(DataEntry(resource).Offset);

    /// <summary>A copy of the resource with another DATA block and the file size in the header to match.</summary>
    /// <exception cref="InvalidDataException">Not a resource, or it has no DATA block at its end.</exception>
    public static byte[] WithData(ReadOnlySpan<byte> resource, ReadOnlySpan<byte> data)
    {
        (int entry, int offset) = DataEntry(resource);
        var copy = new byte[checked(offset + data.Length)];
        resource[..offset].CopyTo(copy);
        data.CopyTo(copy.AsSpan(offset));
        BinaryPrimitives.WriteUInt32LittleEndian(copy, (uint)copy.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(entry + 8), (uint)data.Length);
        return copy;
    }

    /// <summary>The bytes of the first block of that name, wherever it is; null when there is none.</summary>
    /// <exception cref="InvalidDataException">Not a resource, or the block lies outside the file.</exception>
    public static byte[]? Block(ReadOnlySpan<byte> resource, ReadOnlySpan<byte> name)
    {
        if (name.Length != 4)
        {
            throw new ArgumentException("Block names are four bytes", nameof(name));
        }

        foreach (int entry in Entries(resource))
        {
            if (!resource.Slice(entry, 4).SequenceEqual(name))
            {
                continue;
            }

            long offset = entry + 4 + (long)U32(resource, entry + 4);
            long length = U32(resource, entry + 8);
            if (offset + length > resource.Length)
            {
                throw Broken("a block lies outside the file");
            }

            return resource.Slice((int)offset, (int)length).ToArray();
        }

        return null;
    }

    private static (int Entry, int Offset) DataEntry(ReadOnlySpan<byte> resource)
    {
        foreach (int entry in Entries(resource))
        {
            if (!resource.Slice(entry, 4).SequenceEqual("DATA"u8))
            {
                continue;
            }

            long offset = entry + 4 + (long)U32(resource, entry + 4);
            if (offset + U32(resource, entry + 8) != resource.Length)
            {
                throw Broken("the DATA block is not the end of the file");
            }

            return (entry, (int)offset);
        }

        throw Broken("no DATA block");
    }

    /// <summary>Positions of the block table entries.</summary>
    private static List<int> Entries(ReadOnlySpan<byte> resource)
    {
        if (resource.Length < HeaderSize || U32(resource, 0) != resource.Length)
        {
            throw Broken("the size in the header is not the file size");
        }

        // Offsets in the header and in the block table count from the field that holds them.
        long table = 8 + (long)U32(resource, 8);
        long count = U32(resource, 12);
        if (table + (count * BlockEntrySize) > resource.Length)
        {
            throw Broken("block table outside the file");
        }

        var entries = new List<int>((int)count);
        for (long i = 0; i < count; i++)
        {
            entries.Add((int)(table + (i * BlockEntrySize)));
        }

        return entries;
    }

    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);

    private static InvalidDataException Broken(string what) => new($"Not a resource DotaTune can change: {what}");
}
