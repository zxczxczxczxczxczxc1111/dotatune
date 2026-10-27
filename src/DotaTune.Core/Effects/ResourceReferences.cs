using System.Buffers.Binary;
using System.Text;

namespace DotaTune.Core.Effects;

/// <summary>
/// Reads the external references (the RERL block) of a compiled Source 2 resource: the other
/// files it uses, such as child particles and materials. Nothing else in the file is parsed.
/// </summary>
public static class ResourceReferences
{
    private const int HeaderSize = 16;
    private const int BlockEntrySize = 12;
    private const int ReferenceEntrySize = 16;

    /// <summary>Referenced names as the resource stores them, "particles/x.vpcf"; empty when it has no RERL block.</summary>
    /// <exception cref="InvalidDataException">The data is not a resource or an offset points outside it.</exception>
    public static IReadOnlyList<string> Read(ReadOnlySpan<byte> resource)
    {
        if (resource.Length < HeaderSize)
        {
            throw Broken("shorter than the resource header");
        }

        // Offsets in the header and in the block table count from the field that holds them.
        long table = 8 + (long)U32(resource, 8);
        long blockCount = U32(resource, 12);
        if (table + (blockCount * BlockEntrySize) > resource.Length)
        {
            throw Broken("block table outside the file");
        }

        for (long i = 0; i < blockCount; i++)
        {
            int entry = (int)(table + (i * BlockEntrySize));
            if (resource.Slice(entry, 4).SequenceEqual("RERL"u8))
            {
                return ReadList(resource, entry + 4 + (long)U32(resource, entry + 4));
            }
        }

        return [];
    }

    private static List<string> ReadList(ReadOnlySpan<byte> resource, long block)
    {
        if (block + 8 > resource.Length)
        {
            throw Broken("references block outside the file");
        }

        long list = block + U32(resource, (int)block);
        long count = U32(resource, (int)block + 4);
        if (count == 0)
        {
            return [];
        }

        if (list + (count * ReferenceEntrySize) > resource.Length)
        {
            throw Broken("reference list outside the file");
        }

        var names = new List<string>((int)count);
        for (long k = 0; k < count; k++)
        {
            // An entry is the 64-bit id, then a 64-bit offset to the name counted from the offset itself.
            int offsetField = (int)(list + (k * ReferenceEntrySize) + 8);
            long name = offsetField + BinaryPrimitives.ReadInt64LittleEndian(resource[offsetField..]);
            if (name < 0 || name >= resource.Length)
            {
                throw Broken("reference name outside the file");
            }

            int length = resource[(int)name..].IndexOf((byte)0);
            if (length < 0)
            {
                throw Broken("reference name without its terminating zero");
            }

            names.Add(Encoding.UTF8.GetString(resource.Slice((int)name, length)));
        }

        return names;
    }

    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);

    private static InvalidDataException Broken(string what) => new($"Not a readable Source 2 resource: {what}");
}
