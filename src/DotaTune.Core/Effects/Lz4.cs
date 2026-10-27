namespace DotaTune.Core.Effects;

/// <summary>Decodes one LZ4 block, the compression of binary KV3. Only reads: DotaTune writes KV3 uncompressed.</summary>
public static class Lz4
{
    /// <exception cref="InvalidDataException">The block is damaged or does not fill the output exactly.</exception>
    public static void Decode(ReadOnlySpan<byte> source, Span<byte> output)
    {
        int s = 0, d = 0;
        while (s < source.Length)
        {
            int token = source[s++];
            int literals = Length(source, ref s, token >> 4);
            if (literals > source.Length - s || literals > output.Length - d)
            {
                throw Broken("literals run past the end");
            }

            source.Slice(s, literals).CopyTo(output[d..]);
            s += literals;
            d += literals;
            if (s == source.Length)
            {
                break;
            }

            if (source.Length - s < 2)
            {
                throw Broken("match offset cut short");
            }

            int offset = source[s] | (source[s + 1] << 8);
            s += 2;
            int match = Length(source, ref s, token & 0xF) + 4;
            if (offset == 0 || offset > d || match > output.Length - d)
            {
                throw Broken("match outside the output");
            }

            // A match may overlap what it copies, so it goes byte by byte.
            for (int i = 0; i < match; i++, d++)
            {
                output[d] = output[d - offset];
            }
        }

        if (d != output.Length)
        {
            throw Broken($"{d} bytes decoded, {output.Length} expected");
        }
    }

    private static int Length(ReadOnlySpan<byte> source, ref int s, int length)
    {
        if (length != 15)
        {
            return length;
        }

        byte next;
        do
        {
            if (s >= source.Length)
            {
                throw Broken("length cut short");
            }

            next = source[s++];
            length += next;
        }
        while (next == 255);

        return length;
    }

    private static InvalidDataException Broken(string what) => new($"Damaged LZ4 block: {what}");
}
