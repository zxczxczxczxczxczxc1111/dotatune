using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotaTune.Core.Config;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Effects;

namespace DotaTune.Core.Settings;

public sealed record AppSettings
{
    /// <summary>Interface language: "ru", "en", or null to follow Windows.</summary>
    public string? Language { get; init; }

    /// <summary>Effect groups the pack replaces with the empty particle; null means every group.</summary>
    [JsonConverter(typeof(EffectGroupListConverter))]
    public IReadOnlyList<EffectGroup>? EffectGroups { get; init; }

    /// <summary>Choice for autoexec.cfg; null until something is chosen.</summary>
    public GameConfig? Config { get; init; }

    /// <summary>Main menu background: null for the game's own, otherwise the id of a choice on the "Menu background" page.</summary>
    public string? MenuBackground { get; init; }

    /// <summary>Own colours of the mouse effects; null until something is chosen.</summary>
    public MouseColorChoice? MouseColors { get; init; }

    /// <summary>The pack puts the MMR line into the menu and the profile.</summary>
    public bool MmrLine { get; init; }

    /// <summary>The pack accepts a found match on its own.</summary>
    public bool AutoAccept { get; init; }

    /// <summary>Seconds before the match is accepted; null for the default. Kept while the switch is off.</summary>
    public int? AutoAcceptDelay { get; init; }

    /// <summary>The settings copy saved last, offered for a restore without picking a file.</summary>
    public string? LastBackup { get; init; }
}

/// <summary>
/// Writes groups by name and skips a name it does not know (a newer or an older version wrote
/// it), so one such name does not throw the whole settings file away.
/// </summary>
internal sealed class EffectGroupListConverter : JsonConverter<IReadOnlyList<EffectGroup>>
{
    public override IReadOnlyList<EffectGroup>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("effectGroups is not an array");
        }

        var groups = new List<EffectGroup>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            string? name = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            // Exact names only: TryParse alone also takes "3" and "Weather, Sprays".
            if (Enum.TryParse(name, ignoreCase: false, out EffectGroup group) && string.Equals(group.ToString(), name, StringComparison.Ordinal))
            {
                groups.Add(group);
            }
            else
            {
                reader.Skip();
            }
        }

        return groups;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<EffectGroup> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartArray();
        foreach (EffectGroup group in value)
        {
            writer.WriteStringValue(group.ToString());
        }

        writer.WriteEndArray();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Reads and writes settings.json in UTF-8 without BOM.</summary>
public sealed class SettingsStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string _file;
    private readonly FileLog _log;

    public SettingsStore(string file, FileLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(log);
        _file = file;
        _log = log;
    }

    /// <summary>Returns defaults when the file is missing or unreadable; a broken file is logged and left as is.</summary>
    public AppSettings Load()
    {
        if (!File.Exists(_file))
        {
            return new AppSettings();
        }

        try
        {
            string json = File.ReadAllText(_file, Utf8NoBom);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Warning($"Settings file {_file} is unreadable, defaults are used: {ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>Writes through a temporary file so a crash never leaves half a file behind.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings), Utf8NoBom);
        File.Move(temp, _file, overwrite: true);
    }
}
