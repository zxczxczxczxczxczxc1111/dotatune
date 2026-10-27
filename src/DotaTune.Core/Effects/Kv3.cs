namespace DotaTune.Core.Effects;

/// <summary>What a KV3 string refers to. The numbers are those of binary KV3 version 3 and later.</summary>
public enum Kv3Flag : byte
{
    None = 0,
    Resource = 1,
    ResourceName = 2,
    Panorama = 3,
    SoundEvent = 4,
    SubClass = 5,
    EntityName = 6,
    Localize = 7,
}

/// <summary>How binary KV3 stores a value. The numbers are Valve's.</summary>
public enum Kv3Type : byte
{
    Null = 1,
    Boolean = 2,
    Int64 = 3,
    UInt64 = 4,
    Double = 5,
    String = 6,
    Blob = 7,
    Array = 8,
    Object = 9,
    TypedArray = 10,
    Int32 = 11,
    UInt32 = 12,
    True = 13,
    False = 14,
    Int64Zero = 15,
    Int64One = 16,
    DoubleZero = 17,
    DoubleOne = 18,
    Float = 19,
    Int16 = 20,
    UInt16 = 21,
    Int8 = 22,
    UInt8 = 23,

    /// <summary>A typed array with its length in one byte.</summary>
    ShortTypedArray = 24,

    /// <summary>Version 5: a short typed array whose elements sit in the first buffer.</summary>
    AuxiliaryTypedArray = 25,
    UInt32Zero = 26,
    UInt32One = 27,
}

/// <summary>A member of a KV3 object; names may repeat, the last one counts.</summary>
public sealed record Kv3Member(string Name, Kv3Value Value);

/// <summary>A KV3 block: the format it follows and its root value.</summary>
public sealed record Kv3Document(Guid Format, Kv3Value Root);

/// <summary>
/// A value of binary KV3 together with how it was stored, so that writing it back keeps the
/// encoding the game wrote everywhere a value was not changed.
/// </summary>
public sealed class Kv3Value
{
    private long _integer;
    private double _real;

    private Kv3Value(Kv3Type type, Kv3Flag flag)
    {
        Type = type;
        Flag = flag;
    }

    public Kv3Type Type { get; private set; }

    public Kv3Flag Flag { get; }

    /// <summary>The text of a string value; null for other values.</summary>
    public string? Text { get; private init; }

    /// <summary>Elements of an array of any kind; null for other values.</summary>
    public List<Kv3Value>? Items { get; private init; }

    /// <summary>Members of an object in stored order; null for other values.</summary>
    public List<Kv3Member>? Members { get; private init; }

    /// <summary>How every element of a typed array is stored.</summary>
    public Kv3Type ElementType { get; private set; }

    public Kv3Flag ElementFlag { get; private init; }

    public bool IsArray => Items is not null;

    public bool IsNumber => IsInteger(Type) || IsReal(Type);

    /// <summary>The value of a number; 0 for anything else.</summary>
    public double Number => IsReal(Type) ? _real : IsUnsigned(Type) ? (ulong)_integer : _integer;

    /// <summary>The last member with this name, or null.</summary>
    public Kv3Value? this[string name] => Members?.LastOrDefault(m => m.Name == name)?.Value;

    /// <summary>The numbers of an array whose every element is a number; null otherwise.</summary>
    public double[]? Numbers => Items is { Count: > 0 } items && items.All(i => i.IsNumber) ? [.. items.Select(i => i.Number)] : null;

    internal long RawInteger => _integer;

    internal double RawReal => _real;

    /// <summary>Puts a value under a name: in place of the last member with that name, or at the end.</summary>
    public void Set(string name, Kv3Value value)
    {
        ArgumentNullException.ThrowIfNull(value);
        List<Kv3Member> members = Members ?? throw new InvalidOperationException("Only an object has members");
        int at = members.FindLastIndex(m => m.Name == name);
        if (at < 0)
        {
            members.Add(new Kv3Member(name, value));
        }
        else
        {
            members[at] = new Kv3Member(name, value);
        }
    }

    /// <summary>
    /// Replaces the numbers of a numeric array of the same length. The stored type stays where every
    /// new number fits it; otherwise the array widens to 64-bit integers or doubles.
    /// </summary>
    public void SetNumbers(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (Items is not { } items || Numbers is null || items.Count != values.Count)
        {
            throw new InvalidOperationException($"Expected a numeric array of {values.Count}");
        }

        if (Type is Kv3Type.Array)
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].SetNumber(values[i]);
            }

            return;
        }

        Kv3Type element = values.All(v => Fits(ElementType, v)) ? ElementType : Widened(ElementType, values);
        ElementType = element;
        for (int i = 0; i < items.Count; i++)
        {
            items[i].Store(element, values[i]);
        }
    }

    /// <summary>Changes a number, keeping its stored type where the new value fits it.</summary>
    public void SetNumber(double value)
    {
        if (!IsNumber)
        {
            throw new InvalidOperationException($"{Type} is not a number");
        }

        Kv3Type type = Type switch
        {
            Kv3Type.Int64Zero or Kv3Type.Int64One => Compact(Kv3Type.Int64, value),
            Kv3Type.UInt32Zero or Kv3Type.UInt32One => Compact(Kv3Type.UInt32, value),
            Kv3Type.DoubleZero or Kv3Type.DoubleOne => Compact(Kv3Type.Double, value),
            Kv3Type.Int64 or Kv3Type.UInt32 or Kv3Type.Double => Compact(Type, value),
            _ => Type,
        };

        Store(Fits(type, value) ? type : Widened(type, [value]), value);
    }

    /// <summary>A copy of this value and everything in it.</summary>
    public Kv3Value Clone()
    {
        var copy = new Kv3Value(Type, Flag)
        {
            Text = Text,
            Items = Items?.Select(i => i.Clone()).ToList(),
            Members = Members?.Select(m => new Kv3Member(m.Name, m.Value.Clone())).ToList(),
            ElementFlag = ElementFlag,
        };
        copy.ElementType = ElementType;
        copy._integer = _integer;
        copy._real = _real;
        return copy;
    }

    /// <summary>A vector the way compiled particles store one: a short typed array of doubles.</summary>
    public static Kv3Value Doubles(params double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Array(Kv3Type.ShortTypedArray, Kv3Flag.None, [.. values.Select(v => Scalar(Kv3Type.Double, Kv3Flag.None, real: v))], Kv3Type.Double);
    }

    internal static Kv3Value Scalar(Kv3Type type, Kv3Flag flag, long integer = 0, double real = 0) =>
        new(type, flag) { _integer = integer, _real = real };

    internal static Kv3Value String(Kv3Flag flag, string text) => new(Kv3Type.String, flag) { Text = text };

    internal static Kv3Value Array(Kv3Type type, Kv3Flag flag, List<Kv3Value> items, Kv3Type elementType = 0, Kv3Flag elementFlag = Kv3Flag.None)
    {
        var array = new Kv3Value(type, flag) { Items = items, ElementFlag = elementFlag };
        array.ElementType = elementType;
        return array;
    }

    internal static Kv3Value Object(Kv3Flag flag, List<Kv3Member> members) => new(Kv3Type.Object, flag) { Members = members };

    internal static bool IsInteger(Kv3Type type) => type is Kv3Type.Int64 or Kv3Type.UInt64 or Kv3Type.Int32 or Kv3Type.UInt32
        or Kv3Type.Int16 or Kv3Type.UInt16 or Kv3Type.Int8 or Kv3Type.UInt8
        or Kv3Type.Int64Zero or Kv3Type.Int64One or Kv3Type.UInt32Zero or Kv3Type.UInt32One;

    internal static bool IsReal(Kv3Type type) => type is Kv3Type.Double or Kv3Type.Float or Kv3Type.DoubleZero or Kv3Type.DoubleOne;

    private static bool IsUnsigned(Kv3Type type) => type is Kv3Type.UInt64 or Kv3Type.UInt32 or Kv3Type.UInt16 or Kv3Type.UInt8;

    // Valve stores 0 and 1 without a value; a changed number takes the short form where it can.
    private static Kv3Type Compact(Kv3Type type, double value) => (type, value) switch
    {
        (Kv3Type.Int64, 0) => Kv3Type.Int64Zero,
        (Kv3Type.Int64, 1) => Kv3Type.Int64One,
        (Kv3Type.UInt32, 0) => Kv3Type.UInt32Zero,
        (Kv3Type.UInt32, 1) => Kv3Type.UInt32One,
        (Kv3Type.Double, _) when BitConverter.DoubleToInt64Bits(value) == 0 => Kv3Type.DoubleZero,
        (Kv3Type.Double, 1) => Kv3Type.DoubleOne,
        _ => type is Kv3Type.Int64Zero or Kv3Type.Int64One ? Kv3Type.Int64
            : type is Kv3Type.UInt32Zero or Kv3Type.UInt32One ? Kv3Type.UInt32
            : type is Kv3Type.DoubleZero or Kv3Type.DoubleOne ? Kv3Type.Double
            : type,
    };

    private static bool Fits(Kv3Type type, double value)
    {
        bool whole = double.IsFinite(value) && Math.Floor(value) == value;
        return type switch
        {
            Kv3Type.Float or Kv3Type.Double => double.IsFinite(value),
            Kv3Type.DoubleZero => BitConverter.DoubleToInt64Bits(value) == 0,
            Kv3Type.DoubleOne or Kv3Type.Int64One or Kv3Type.UInt32One => value == 1,
            Kv3Type.Int64Zero or Kv3Type.UInt32Zero => value == 0,
            Kv3Type.Int8 => whole && value is >= sbyte.MinValue and <= sbyte.MaxValue,
            Kv3Type.UInt8 => whole && value is >= byte.MinValue and <= byte.MaxValue,
            Kv3Type.Int16 => whole && value is >= short.MinValue and <= short.MaxValue,
            Kv3Type.UInt16 => whole && value is >= ushort.MinValue and <= ushort.MaxValue,
            Kv3Type.Int32 => whole && value is >= int.MinValue and <= int.MaxValue,
            Kv3Type.UInt32 => whole && value is >= uint.MinValue and <= uint.MaxValue,
            Kv3Type.Int64 => whole && value is >= -9.2e18 and <= 9.2e18,
            Kv3Type.UInt64 => whole && value is >= 0 and <= 1.8e19,
            _ => false,
        };
    }

    private static Kv3Type Widened(Kv3Type type, IReadOnlyList<double> values) =>
        IsInteger(type) && values.All(v => Fits(Kv3Type.Int64, v)) ? Kv3Type.Int64 : Kv3Type.Double;

    private void Store(Kv3Type type, double value)
    {
        Type = type;
        if (IsReal(type))
        {
            _real = type == Kv3Type.Float ? (float)value : value;
            _integer = 0;
        }
        else
        {
            _integer = IsUnsigned(type) ? unchecked((long)(ulong)value) : (long)value;
            _real = 0;
        }
    }
}
