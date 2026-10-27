using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotaTune.Core.Config;

/// <summary>A colour the way the game takes it: three numbers from 0 to 255.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Hex => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");

    /// <summary>"R G B", the value dota_*_color takes.</summary>
    public string CommandValue => string.Create(CultureInfo.InvariantCulture, $"{R} {G} {B}");

    /// <summary>"#RRGGBB" or "RRGGBB"; null for anything else.</summary>
    public static Rgb? ParseHex(string? text)
    {
        string digits = (text ?? string.Empty).Trim().TrimStart('#');
        if (digits.Length != 6 || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
        {
            return null;
        }

        return new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}

/// <summary>One line of autoexec.cfg: a command and its value.</summary>
public sealed record ConfigLine(string Name, string Value)
{
    public override string ToString() => $"{Name} {Value}";
}

/// <summary>
/// What was chosen for autoexec.cfg on the "Config and launch" page. Null or false means
/// "as the game has it", and no line is written for it.
/// </summary>
public sealed record GameConfig
{
    /// <summary>Creeps and buildings of the own side on the minimap.</summary>
    [JsonConverter(typeof(RgbJsonConverter))]
    public Rgb? FriendlyColor { get; init; }

    [JsonConverter(typeof(RgbJsonConverter))]
    public Rgb? EnemyColor { get; init; }

    /// <summary>Neutral creeps and Roshan.</summary>
    [JsonConverter(typeof(RgbJsonConverter))]
    public Rgb? NeutralColor { get; init; }

    /// <summary>Size of creep dots on the minimap; the game has 1.</summary>
    public double? CreepScale { get; init; }

    public bool HideFrontpageVideo { get; init; }

    /// <summary>Frame limit outside a match; the game has none.</summary>
    public int? MenuFpsLimit { get; init; }

    public bool NoParticleLights { get; init; }

    /// <summary>The lines of <see cref="MinimalismPreset"/> go into the block after the others, without a name already there.</summary>
    public bool Minimalism { get; init; }

    /// <summary>Lines of the minimap colours, which the colours page shows apart from the rest of the block.</summary>
    public static IReadOnlyList<string> ColorNames { get; } = ["dota_friendly_color", "dota_enemy_color", "dota_neutral_color"];

    public static IReadOnlyList<double> CreepScales { get; } = [1.25, 1.5, 1.75, 2];

    public static IReadOnlyList<int> MenuFpsLimits { get; } = [60, 120, 144, 165, 240];

    /// <summary>The same choice with every value that would give no line set to "as in the game".</summary>
    public GameConfig Sanitized() => this with
    {
        CreepScale = CreepScale is >= 0.5 and <= 3 and not 1 ? CreepScale : null,
        MenuFpsLimit = MenuFpsLimit is >= 30 and <= 1000 ? MenuFpsLimit : null,
    };

    /// <summary>The same choice with the size and the frame limit moved to the nearest value on offer.</summary>
    public GameConfig Snapped() => this with
    {
        CreepScale = CreepScale is double scale ? CreepScales.MinBy(s => Math.Abs(s - scale)) : null,
        MenuFpsLimit = MenuFpsLimit is int fps ? MenuFpsLimits.MinBy(f => Math.Abs(f - fps)) : null,
    };

    /// <summary>
    /// The lines of this choice in a fixed order. The minimap commands are development-only
    /// and were checked in the game on 26.10.2026; the other three are ordinary ones.
    /// A value outside the allowed range, as from a hand-edited file, gives no line.
    /// </summary>
    public IReadOnlyList<ConfigLine> Lines()
    {
        var lines = new List<ConfigLine>();
        AddColor(ColorNames[0], FriendlyColor);
        AddColor(ColorNames[1], EnemyColor);
        AddColor(ColorNames[2], NeutralColor);
        if (CreepScale is double scale && scale is >= 0.5 and <= 3 && scale != 1)
        {
            lines.Add(new ConfigLine("dota_minimap_creep_scale", scale.ToString("0.##", CultureInfo.InvariantCulture)));
        }

        if (HideFrontpageVideo)
        {
            lines.Add(new ConfigLine("dota_hide_frontpage_video", "1"));
        }

        if (MenuFpsLimit is int fps && fps is >= 30 and <= 1000)
        {
            lines.Add(new ConfigLine("fps_max_ui", fps.ToString(CultureInfo.InvariantCulture)));
        }

        if (NoParticleLights)
        {
            lines.Add(new ConfigLine("dota_disable_particle_lights", "1"));
        }

        if (Minimalism)
        {
            // dota_disable_particle_lights is also a switch of its own: one line, whichever set it.
            HashSet<string> taken = [.. lines.Select(l => l.Name)];
            lines.AddRange(MinimalismPreset.Lines.Where(l => !taken.Contains(l.Name)));
        }

        return lines;

        void AddColor(string name, Rgb? color)
        {
            if (color is Rgb value)
            {
                lines.Add(new ConfigLine(name, value.CommandValue));
            }
        }
    }
}

/// <summary>Colours are stored as "#RRGGBB"; a value it cannot read becomes "as in the game" instead of breaking the settings.</summary>
internal sealed class RgbJsonConverter : JsonConverter<Rgb?>
{
    public override bool HandleNull => true;

    public override Rgb? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return Rgb.ParseHex(reader.GetString());
        }

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, Rgb? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is Rgb color)
        {
            writer.WriteStringValue(color.Hex);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
