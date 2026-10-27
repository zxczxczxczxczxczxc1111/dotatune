using System.Buffers.Binary;
using System.Text;

namespace DotaTune.Core.Effects;

/// <summary>
/// Binary KV3, the data block of compiled particles. Reads versions 2 to 5, uncompressed or LZ4,
/// the way the game stores them in build 6967; writes version 4 uncompressed, which the game also
/// ships (particles/dev/empty_particle.vpcf_c among others). Binary blobs are not supported:
/// particles have none.
/// </summary>
public static class Kv3Binary
{
    private const uint Magic = 0x4B563300;
    private const uint Trailer = 0xFFEEDD00;
    private const int WrittenVersion = 4;
    private const int MaxCount = 1 << 20;

    /// <exception cref="InvalidDataException">Not binary KV3 this reader knows, or damaged.</exception>
    public static Kv3Document Read(ReadOnlySpan<byte> block)
    {
        try
        {
            return new Reader(block).Document;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException($"Not readable binary KV3: {ex.Message}", ex);
        }
    }

    /// <summary>The block as version 4 without compression.</summary>
    /// <exception cref="InvalidDataException">The value cannot be stored, such as a typed array left empty.</exception>
    public static byte[] Write(Kv3Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new Writer(document).Bytes;
    }

    private static InvalidDataException Broken(string what) => new($"Not readable binary KV3: {what}");

    /// <summary>One lane of values of the same size, read in order.</summary>
    private sealed class Lane
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _at;

        public Lane(byte[] data, int start, int length)
        {
            if (start < 0 || length < 0 || length > data.Length - start)
            {
                throw Broken("lane outside the buffer");
            }

            _data = data;
            _at = start;
            _end = start + length;
        }

        public bool IsEmpty => _at == _end;

        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _end - _at)
            {
                throw Broken("a value outside its lane");
            }

            ReadOnlySpan<byte> span = _data.AsSpan(_at, count);
            _at += count;
            return span;
        }

        public byte U8() => Take(1)[0];

        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    }

    private sealed class Lanes
    {
        public Lane B1 = Empty;
        public Lane B2 = Empty;
        public Lane B4 = Empty;
        public Lane B8 = Empty;

        public static readonly Lane Empty = new([], 0, 0);
    }

    private sealed class Reader
    {
        private readonly int _version;
        private readonly Lanes _main = new();
        private readonly Lanes _aux;
        private readonly Lane _types;
        private readonly Lane _objectLengths = Lanes.Empty;
        private readonly string[] _strings;

        public Reader(ReadOnlySpan<byte> block)
        {
            uint magic = block.Length >= 4 ? U32(block, 0) : 0;
            _version = (int)(magic & 0xFF);
            if ((magic & 0xFFFFFF00) != Magic || _version is < 2 or > 5)
            {
                throw Broken($"signature {magic:X8} is not KV3 version 2 to 5");
            }

            if (block.Length < _version switch { 4 => 72, 5 => 120, _ => 64 })
            {
                throw Broken("shorter than the header");
            }

            var format = new Guid(block.Slice(4, 16));
            uint method = U32(block, 20);
            if (method > 1 || BinaryPrimitives.ReadUInt16LittleEndian(block[24..]) != 0)
            {
                throw Broken($"compression {method} with a dictionary is not supported");
            }

            int countBytes1 = I32(block, 28), countBytes4 = I32(block, 32), countBytes8 = I32(block, 36), countTypes = I32(block, 40);
            int sizeTotal = I32(block, 48), compressedTotal = I32(block, 52), countBlobs = I32(block, 56);
            if (countBlobs != 0)
            {
                throw Broken("binary blobs are not supported");
            }

            int countBytes2 = 0, at = 64;
            if (_version >= 4)
            {
                countBytes2 = I32(block, 64);
                at = 72;
            }

            if (_version < 5)
            {
                byte[] buffer = Unpack(block, ref at, method, sizeTotal, compressedTotal);
                int offset = Layout(buffer, 0, _main, countBytes1, countBytes2, countBytes4, countBytes8, alignEmpty8: true);
                _strings = ReadStrings(buffer, ref offset, _main.B4.I32(), out int stringsLength);
                _types = new Lane(buffer, offset, countTypes - stringsLength);
                CheckTrailer(buffer, offset + countTypes - stringsLength);
                _aux = _main;
            }
            else
            {
                int size1 = I32(block, 72), compressed1 = I32(block, 76), size2 = I32(block, 80), compressed2 = I32(block, 84);
                int countObjects = I32(block, 108);
                at = 120;
                byte[] buffer1 = Unpack(block, ref at, method, size1, compressed1);
                byte[] buffer2 = Unpack(block, ref at, method, size2, compressed2);

                // The first buffer holds the strings and the values of auxiliary arrays, the second everything else.
                _aux = new Lanes();
                Layout(buffer1, 0, _aux, countBytes1, countBytes2, countBytes4, countBytes8, alignEmpty8: false);
                int stringsAt = 0;
                _strings = ReadStrings(buffer1, ref stringsAt, _aux.B4.I32(), out int stringsLength);
                _aux.B1.Take(stringsLength);

                _objectLengths = new Lane(buffer2, 0, checked(countObjects * 4));
                int offset = Layout(buffer2, countObjects * 4, _main, I32(block, 88), I32(block, 92), I32(block, 96), I32(block, 100), alignEmpty8: false);
                _types = new Lane(buffer2, offset, countTypes);
                CheckTrailer(buffer2, offset + countTypes);
            }

            (Kv3Type type, Kv3Flag flag) = ReadType();
            Document = new Kv3Document(format, ReadValue(type, flag, _main));
            if (!_types.IsEmpty)
            {
                throw Broken("types left over after the root value");
            }
        }

        public Kv3Document Document { get; }

        private static byte[] Unpack(ReadOnlySpan<byte> block, ref int at, uint method, int size, int compressed)
        {
            if (size < 0)
            {
                throw Broken("negative buffer size");
            }

            int stored = method == 0 ? size : compressed;
            if (stored < 0 || stored > block.Length - at)
            {
                throw Broken("buffer outside the block");
            }

            var buffer = new byte[size];
            if (method == 0)
            {
                block.Slice(at, size).CopyTo(buffer);
            }
            else
            {
                Lz4.Decode(block.Slice(at, stored), buffer);
            }

            at += stored;
            return buffer;
        }

        private static int Layout(byte[] buffer, int offset, Lanes lanes, int count1, int count2, int count4, int count8, bool alignEmpty8)
        {
            lanes.B1 = Slice(buffer, ref offset, 1, count1);
            lanes.B2 = Slice(buffer, ref offset, 2, count2);
            lanes.B4 = Slice(buffer, ref offset, 4, count4);
            if (count8 > 0 || alignEmpty8)
            {
                // Before version 5 the eight-byte lane is aligned even when empty.
                offset = Align(offset, 8);
            }

            lanes.B8 = Slice(buffer, ref offset, 8, count8);
            return offset;
        }

        private static Lane Slice(byte[] buffer, ref int offset, int size, int count)
        {
            if (count == 0)
            {
                return Lanes.Empty;
            }

            offset = Align(offset, size);
            int length = checked(count * size);
            if (count < 0 || offset + length > buffer.Length)
            {
                throw Broken("lane outside the buffer");
            }

            var lane = new Lane(buffer, offset, length);
            offset += length;
            return lane;
        }

        private static string[] ReadStrings(byte[] buffer, ref int offset, int count, out int length)
        {
            if (count < 0 || count > buffer.Length)
            {
                throw Broken("string count");
            }

            var strings = new string[count];
            int start = offset;
            for (int i = 0; i < count; i++)
            {
                int end = buffer.AsSpan(offset).IndexOf((byte)0);
                if (end < 0)
                {
                    throw Broken("string without its terminating zero");
                }

                strings[i] = Encoding.UTF8.GetString(buffer, offset, end);
                offset += end + 1;
            }

            length = offset - start;
            return strings;
        }

        private static void CheckTrailer(byte[] buffer, int at)
        {
            if (at < 0 || at + 4 > buffer.Length || U32(buffer, at) != Trailer)
            {
                throw Broken("no end marker after the types");
            }
        }

        private (Kv3Type Type, Kv3Flag Flag) ReadType()
        {
            byte type = _types.U8();
            var flag = Kv3Flag.None;
            if (_version >= 3)
            {
                if ((type & 0x80) != 0)
                {
                    flag = (Kv3Flag)_types.U8();
                    if (flag is Kv3Flag.None or > Kv3Flag.Localize)
                    {
                        throw Broken($"flag {(byte)flag}");
                    }
                }

                // No known writer sets this bit; readers skip the byte after it.
                if ((type & 0x40) != 0)
                {
                    _types.U8();
                }

                type &= 0x3F;
            }
            else
            {
                if ((type & 0x80) != 0)
                {
                    flag = LegacyFlag(_types.U8());
                }

                type &= 0x7F;
            }

            if (type is 0 or > (byte)Kv3Type.UInt32One)
            {
                throw Broken($"type {type}");
            }

            return ((Kv3Type)type, flag);
        }

        // Before version 3 flags were bits and only one could be kept: the lowest wins, bit 4 marked multiline text.
        private static Kv3Flag LegacyFlag(byte bits) =>
            (bits & 0x80) != 0 ? throw Broken($"flag {bits}")
            : (bits & 1) != 0 ? Kv3Flag.Resource
            : (bits & 2) != 0 ? Kv3Flag.ResourceName
            : (bits & 8) != 0 ? Kv3Flag.Panorama
            : (bits & 16) != 0 ? Kv3Flag.SoundEvent
            : (bits & 32) != 0 ? Kv3Flag.SubClass
            : Kv3Flag.None;

        // -1 is the empty string; any other id outside the table means the block is damaged.
        private string Text(int id) =>
            id == -1 ? string.Empty
            : (uint)id < (uint)_strings.Length ? _strings[id]
            : throw Broken($"string {id} of {_strings.Length}");

        // Numbers come from the given lanes, which are the auxiliary ones for elements of an
        // auxiliary array; strings and container lengths always come from the main lanes.
        private Kv3Value ReadValue(Kv3Type type, Kv3Flag flag, Lanes lanes)
        {
            switch (type)
            {
                case Kv3Type.Null or Kv3Type.True or Kv3Type.False or Kv3Type.Int64Zero or Kv3Type.UInt32Zero:
                    return Kv3Value.Scalar(type, flag);
                case Kv3Type.Int64One or Kv3Type.UInt32One:
                    return Kv3Value.Scalar(type, flag, integer: 1);
                case Kv3Type.DoubleZero:
                    return Kv3Value.Scalar(type, flag);
                case Kv3Type.DoubleOne:
                    return Kv3Value.Scalar(type, flag, real: 1);
                case Kv3Type.Boolean or Kv3Type.UInt8:
                    return Kv3Value.Scalar(type, flag, integer: lanes.B1.U8());
                case Kv3Type.Int8:
                    return Kv3Value.Scalar(type, flag, integer: (sbyte)lanes.B1.U8());
                case Kv3Type.Int16:
                    return Kv3Value.Scalar(type, flag, integer: BinaryPrimitives.ReadInt16LittleEndian(lanes.B2.Take(2)));
                case Kv3Type.UInt16:
                    return Kv3Value.Scalar(type, flag, integer: BinaryPrimitives.ReadUInt16LittleEndian(lanes.B2.Take(2)));
                case Kv3Type.Int32:
                    return Kv3Value.Scalar(type, flag, integer: lanes.B4.I32());
                case Kv3Type.UInt32:
                    return Kv3Value.Scalar(type, flag, integer: BinaryPrimitives.ReadUInt32LittleEndian(lanes.B4.Take(4)));
                case Kv3Type.Float:
                    return Kv3Value.Scalar(type, flag, real: BinaryPrimitives.ReadSingleLittleEndian(lanes.B4.Take(4)));
                case Kv3Type.Int64 or Kv3Type.UInt64:
                    return Kv3Value.Scalar(type, flag, integer: BinaryPrimitives.ReadInt64LittleEndian(lanes.B8.Take(8)));
                case Kv3Type.Double:
                    return Kv3Value.Scalar(type, flag, real: BinaryPrimitives.ReadDoubleLittleEndian(lanes.B8.Take(8)));
                case Kv3Type.String:
                    return Kv3Value.String(flag, Text(_main.B4.I32()));
                case Kv3Type.Array:
                {
                    int count = Count(_main.B4.I32());
                    var items = new List<Kv3Value>(count);
                    for (int i = 0; i < count; i++)
                    {
                        (Kv3Type itemType, Kv3Flag itemFlag) = ReadType();
                        items.Add(ReadValue(itemType, itemFlag, _main));
                    }

                    return Kv3Value.Array(type, flag, items);
                }

                case Kv3Type.TypedArray or Kv3Type.ShortTypedArray or Kv3Type.AuxiliaryTypedArray:
                {
                    int count = Count(type == Kv3Type.TypedArray ? _main.B4.I32() : _main.B1.U8());
                    if (count == 0)
                    {
                        throw Broken("empty typed array");
                    }

                    (Kv3Type elementType, Kv3Flag elementFlag) = ReadType();
                    Lanes elementLanes = type == Kv3Type.AuxiliaryTypedArray ? _aux : _main;
                    var items = new List<Kv3Value>(count);
                    for (int i = 0; i < count; i++)
                    {
                        items.Add(ReadValue(elementType, elementFlag, elementLanes));
                    }

                    return Kv3Value.Array(type, flag, items, elementType, elementFlag);
                }

                case Kv3Type.Object:
                {
                    int count = Count(_version >= 5 ? _objectLengths.I32() : _main.B4.I32());
                    var members = new List<Kv3Member>(count);
                    for (int i = 0; i < count; i++)
                    {
                        (Kv3Type memberType, Kv3Flag memberFlag) = ReadType();
                        string name = Text(_main.B4.I32());
                        members.Add(new Kv3Member(name, ReadValue(memberType, memberFlag, _main)));
                    }

                    return Kv3Value.Object(flag, members);
                }

                default:
                    throw Broken($"type {type} is not supported");
            }
        }

        // A damaged count of elements that take no bytes would otherwise run for billions of steps.
        private static int Count(int count) => count is >= 0 and <= MaxCount ? count : throw Broken($"count {count}");
    }

    private sealed class Writer
    {
        private readonly MemoryStream _b1 = new(), _b2 = new(), _b4 = new(), _b8 = new(), _types = new();
        private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];
        private int _objects;
        private int _arrays;

        public Writer(Kv3Document document)
        {
            I32(_b4, 0); // the string count, filled in below
            WriteNode(document.Root);
            byte[] count = BitConverter.GetBytes(_order.Count);
            _b4.Position = 0;
            _b4.Write(count);

            using var buffer = new MemoryStream();
            _b1.WriteTo(buffer);
            Lane(buffer, _b2, 2);
            Lane(buffer, _b4, 4);
            Pad(buffer, 8);
            _b8.WriteTo(buffer);
            long stringsAt = buffer.Length;
            foreach (string text in _order)
            {
                buffer.Write(Encoding.UTF8.GetBytes(text));
                buffer.WriteByte(0);
            }

            _types.WriteTo(buffer);
            int countTypes = checked((int)(buffer.Length - stringsAt));
            U32(buffer, Trailer);

            using var block = new MemoryStream();
            U32(block, Magic | WrittenVersion);
            block.Write(document.Format.ToByteArray());
            U32(block, 0); // no compression
            U16(block, 0); // dictionary
            U16(block, 0); // frame size, only for LZ4
            I32(block, (int)_b1.Length);
            I32(block, (int)_b4.Length / 4);
            I32(block, (int)_b8.Length / 8);
            I32(block, countTypes);
            U16(block, (ushort)Math.Min(_objects, ushort.MaxValue));
            U16(block, (ushort)Math.Min(_arrays, ushort.MaxValue));
            I32(block, (int)buffer.Length);
            I32(block, (int)buffer.Length);
            I32(block, 0); // binary blobs
            I32(block, 0); // their size
            I32(block, (int)_b2.Length / 2);
            I32(block, 0); // compressed blob frame sizes
            buffer.WriteTo(block);
            Bytes = block.ToArray();
        }

        public byte[] Bytes { get; }

        private void WriteNode(Kv3Value value)
        {
            Kv3Type type = value.Type == Kv3Type.AuxiliaryTypedArray
                ? (value.Items!.Count <= byte.MaxValue ? Kv3Type.ShortTypedArray : Kv3Type.TypedArray)
                : value.Type;
            WriteType(type, value.Flag);
            WritePayload(value, type);
        }

        private void WriteType(Kv3Type type, Kv3Flag flag)
        {
            if (flag == Kv3Flag.None)
            {
                _types.WriteByte((byte)type);
            }
            else
            {
                _types.WriteByte((byte)((byte)type | 0x80));
                _types.WriteByte((byte)flag);
            }
        }

        private void WritePayload(Kv3Value value, Kv3Type type)
        {
            switch (type)
            {
                case Kv3Type.Null or Kv3Type.True or Kv3Type.False or Kv3Type.Int64Zero or Kv3Type.Int64One
                    or Kv3Type.DoubleZero or Kv3Type.DoubleOne or Kv3Type.UInt32Zero or Kv3Type.UInt32One:
                    break;
                case Kv3Type.Boolean or Kv3Type.Int8 or Kv3Type.UInt8:
                    _b1.WriteByte(unchecked((byte)value.RawInteger));
                    break;
                case Kv3Type.Int16 or Kv3Type.UInt16:
                    U16(_b2, unchecked((ushort)value.RawInteger));
                    break;
                case Kv3Type.Int32 or Kv3Type.UInt32:
                    U32(_b4, unchecked((uint)value.RawInteger));
                    break;
                case Kv3Type.Float:
                    Span<byte> single = stackalloc byte[4];
                    BinaryPrimitives.WriteSingleLittleEndian(single, (float)value.RawReal);
                    _b4.Write(single);
                    break;
                case Kv3Type.Int64 or Kv3Type.UInt64:
                    Span<byte> integer = stackalloc byte[8];
                    BinaryPrimitives.WriteInt64LittleEndian(integer, value.RawInteger);
                    _b8.Write(integer);
                    break;
                case Kv3Type.Double:
                    Span<byte> real = stackalloc byte[8];
                    BinaryPrimitives.WriteDoubleLittleEndian(real, value.RawReal);
                    _b8.Write(real);
                    break;
                case Kv3Type.String:
                    I32(_b4, StringId(value.Text ?? string.Empty));
                    break;
                case Kv3Type.Array:
                    _arrays++;
                    I32(_b4, value.Items!.Count);
                    foreach (Kv3Value item in value.Items)
                    {
                        WriteNode(item);
                    }

                    break;
                case Kv3Type.TypedArray or Kv3Type.ShortTypedArray:
                    _arrays++;
                    List<Kv3Value> items = value.Items!;
                    if (items.Count == 0 || (type == Kv3Type.ShortTypedArray && items.Count > byte.MaxValue))
                    {
                        throw new InvalidDataException($"A typed array of {items.Count} cannot be stored as {type}");
                    }

                    if (type == Kv3Type.TypedArray)
                    {
                        I32(_b4, items.Count);
                    }
                    else
                    {
                        _b1.WriteByte((byte)items.Count);
                    }

                    // Elements that are themselves auxiliary arrays become short or long ones like any other.
                    Kv3Type element = value.ElementType != Kv3Type.AuxiliaryTypedArray ? value.ElementType
                        : items.All(i => i.Items!.Count <= byte.MaxValue) ? Kv3Type.ShortTypedArray : Kv3Type.TypedArray;
                    WriteType(element, value.ElementFlag);
                    foreach (Kv3Value item in items)
                    {
                        WritePayload(item, element);
                    }

                    break;
                case Kv3Type.Object:
                    _objects++;
                    I32(_b4, value.Members!.Count);
                    foreach (Kv3Member member in value.Members)
                    {
                        I32(_b4, StringId(member.Name));
                        WriteNode(member.Value);
                    }

                    break;
                default:
                    throw new InvalidDataException($"{type} cannot be written");
            }
        }

        private int StringId(string text)
        {
            if (text.Length == 0)
            {
                return -1;
            }

            if (!_strings.TryGetValue(text, out int id))
            {
                id = _order.Count;
                _strings[text] = id;
                _order.Add(text);
            }

            return id;
        }

        private static void Lane(MemoryStream buffer, MemoryStream lane, int alignment)
        {
            if (lane.Length > 0)
            {
                Pad(buffer, alignment);
                lane.WriteTo(buffer);
            }
        }

        private static void Pad(MemoryStream stream, int alignment)
        {
            while (stream.Length % alignment != 0)
            {
                stream.WriteByte(0);
            }
        }

        private static void U16(MemoryStream stream, ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            stream.Write(bytes);
        }

        private static void U32(MemoryStream stream, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            stream.Write(bytes);
        }

        private static void I32(MemoryStream stream, int value) => U32(stream, unchecked((uint)value));
    }

    private static int Align(int offset, int alignment) => (offset + alignment - 1) / alignment * alignment;

    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);

    private static int I32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
}
