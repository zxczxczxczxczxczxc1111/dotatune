using ValveKeyValue;

namespace DotaTune.Core.Steam;

/// <summary>Reading of Steam and game text files in KeyValues format (.vdf, .acf, .vcfg).</summary>
public static class KeyValues
{
    /// <summary>Steam writes paths with doubled backslashes, so escape sequences are on.</summary>
    private static readonly KVSerializerOptions Options = new() { HasEscapeSequences = true };

    /// <exception cref="KeyValuesException">The file is not valid KeyValues text.</exception>
    public static KVObject Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        try
        {
            return KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream, Options).Root;
        }
        catch (Exception ex) when (ex is KeyValueException or InvalidDataException or FormatException)
        {
            throw new KeyValuesException(path, ex);
        }
    }

    /// <summary>Child by name, ignoring case: Steam writes both "apps" and "Apps".</summary>
    public static KVObject? Child(this KVObject parent, string name)
    {
        ArgumentNullException.ThrowIfNull(parent);
        foreach (KeyValuePair<string, KVObject> child in parent)
        {
            if (string.Equals(child.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return child.Value;
            }
        }

        return null;
    }

    /// <summary>Follows a chain of child names; null as soon as one is missing.</summary>
    public static KVObject? Path(this KVObject root, params ReadOnlySpan<string> names)
    {
        KVObject? current = root;
        foreach (string name in names)
        {
            current = current?.Child(name);
        }

        return current;
    }

    /// <summary>Scalar value as text, or null for a missing key or a nested block.</summary>
    public static string? Text(this KVObject? value) =>
        value is null || value.IsCollection ? null : (string)value;
}

public sealed class KeyValuesException(string path, Exception inner)
    : Exception($"{path} is not a valid KeyValues file: {inner.Message}", inner)
{
    public string FilePath { get; } = path;
}
