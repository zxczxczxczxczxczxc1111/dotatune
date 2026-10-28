using System.Text.Json.Serialization;
using DotaTune.Core.Config;
using DotaTune.Core.Game;
using ValvePak;

namespace DotaTune.Core.Effects;

/// <summary>Effects of the mouse the "Clicks and selection" page can recolour.</summary>
public enum MouseEffect
{
    /// <summary>Marker of a move order on the ground.</summary>
    MoveClick,

    /// <summary>Marker of an attack-move order.</summary>
    AttackClick,

    /// <summary>Ring on the unit an order is given on, such as the enemy being attacked.</summary>
    UnitClick,

    /// <summary>Rings under selected units and the ones a selection box leaves.</summary>
    SelectionRings,

    /// <summary>Highlight that shows where the own hero is.</summary>
    HeroHighlight,

    /// <summary>Circle of a tower's attack range and the flash when the tower takes the hero as its target.</summary>
    TowerRange,
}

/// <summary>A colour per mouse effect; null keeps the game's colour.</summary>
public sealed record MouseColorChoice
{
    [JsonConverter(typeof(RgbJsonConverter))]
    public Rgb? MoveClick { get; init; }

    [JsonConverter(typeof(RgbJsonConverter))]
    public Rgb? AttackClick { get; init; }

    [JsonConverter(typeof(RgbJsonConverter))]
    public Rgb? UnitClick { get; init; }

    [JsonConverter(typeof(RgbJsonConverter))]
    public Rgb? SelectionRings { get; init; }

    [JsonConverter(typeof(RgbJsonConverter))]
    public Rgb? HeroHighlight { get; init; }

    [JsonConverter(typeof(RgbJsonConverter))]
    public Rgb? TowerRange { get; init; }

    [JsonIgnore]
    public bool IsEmpty => Chosen.Count == 0;

    /// <summary>The effects with an own colour, in the order of <see cref="MouseEffect"/>.</summary>
    [JsonIgnore]
    public IReadOnlyList<KeyValuePair<MouseEffect, Rgb>> Chosen =>
        [.. Enum.GetValues<MouseEffect>().Where(e => this[e] is not null).Select(e => KeyValuePair.Create(e, this[e]!.Value))];

    public Rgb? this[MouseEffect effect] => effect switch
    {
        MouseEffect.MoveClick => MoveClick,
        MouseEffect.AttackClick => AttackClick,
        MouseEffect.UnitClick => UnitClick,
        MouseEffect.SelectionRings => SelectionRings,
        MouseEffect.HeroHighlight => HeroHighlight,
        MouseEffect.TowerRange => TowerRange,
        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, null),
    };

    public MouseColorChoice With(MouseEffect effect, Rgb? color) => effect switch
    {
        MouseEffect.MoveClick => this with { MoveClick = color },
        MouseEffect.AttackClick => this with { AttackClick = color },
        MouseEffect.UnitClick => this with { UnitClick = color },
        MouseEffect.SelectionRings => this with { SelectionRings = color },
        MouseEffect.HeroHighlight => this with { HeroHighlight = color },
        MouseEffect.TowerRange => this with { TowerRange = color },
        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, null),
    };
}

/// <summary>Recoloured copies of the game's mouse particles and how many files each effect got.</summary>
/// <param name="Problems">Particles that stay as in the game and why: a root the game no longer has, an unreadable file.</param>
public sealed record MouseRecolor(IReadOnlyList<PackedFile> Files, IReadOnlyDictionary<MouseEffect, int> Counts, IReadOnlyList<string> Problems);

/// <summary>
/// Recolours the game's own mouse particles, as Dota 2 Mod Manager does, for any colour. Checked
/// on build 6967: the game passes the colour of a click or a ring in control point 1, and each
/// particle turns that point into the colour of its particles through a remap; some layers draw a
/// colour of their own. Both kinds become the chosen colour. The tower range (build 6969) gets from
/// the game in point 4 a colour going from yellow to red with the danger and sets it as it is; that
/// input becomes the chosen colour too. Particles are read from the current game, so a patch that
/// changes them is picked up on the next build of the pack.
/// </summary>
public static class MouseEffects
{
    private const string Folder = "particles/ui_mouseactions/";

    /// <summary>The particle attribute that holds the colour.</summary>
    private const int ColorField = 6;

    /// <summary>
    /// The particles the game starts for each effect (names from client.dll of build 6967); their
    /// children come along. The move marker has two looks, chosen by
    /// dota_use_occluded_click_particle_effects: both are recoloured. The tower range has two looks
    /// as well and a flash the game starts with the sound Building_Tower.Aggro (build 6969);
    /// range_finder_tower_aoe stays, abilities of heroes use it too.
    /// </summary>
    public static IReadOnlyList<string> Roots(MouseEffect effect) => effect switch
    {
        MouseEffect.MoveClick => [Folder + "clicked_occlusion_basemove.vpcf", Folder + "clicked_basemove.vpcf", Folder + "clicked_moveto.vpcf"],
        MouseEffect.AttackClick => [Folder + "clicked_occlusion_basemove_attack.vpcf"],
        MouseEffect.UnitClick => [Folder + "clicked_unit_select.vpcf"],
        MouseEffect.SelectionRings => [Folder + "selected_ring.vpcf", Folder + "drag_selected_ring.vpcf", Folder + "drag_selected_ring_reselect.vpcf", Folder + "selected_ring_hit.vpcf"],
        MouseEffect.HeroHighlight => [Folder + "hero_highlighter.vpcf", Folder + "hero_highlighter_playerglow.vpcf"],
        MouseEffect.TowerRange => [Folder + "tower_range_indicator_default.vpcf", Folder + "tower_range_indicator_alt.vpcf", Folder + "range_finder_tower_experimental_pulse.vpcf"],
        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, null),
    };

    /// <summary>Reads game\dota\pak01_dir.vpk and recolours the particles of every chosen effect. Only reads.</summary>
    /// <exception cref="InvalidDataException">The archive cannot be read.</exception>
    public static MouseRecolor Read(string dotaFolder, MouseColorChoice choice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dotaFolder);
        ArgumentNullException.ThrowIfNull(choice);
        string pak = Path.Combine(dotaFolder, "pak01_dir.vpk");
        if (!File.Exists(pak))
        {
            throw new FileNotFoundException("The game has no pak01_dir.vpk", pak);
        }

        try
        {
            using var package = new Package();
            package.Read(pak);
            return Recolor(path => package.FindEntry(path) is { } entry ? ReadEntry(package, entry) : null, choice);
        }
        catch (Exception ex) when (GameArchive.IsDamage(ex))
        {
            throw new InvalidDataException($"{pak} is not readable: {ex.Message}", ex);
        }
    }

    /// <param name="read">Reads a compiled file of the game by its path; null when there is none.</param>
    internal static MouseRecolor Recolor(Func<string, byte[]?> read, MouseColorChoice choice)
    {
        var problems = new List<string>();
        var resources = new Dictionary<string, byte[]?>(StringComparer.Ordinal);

        // Every effect is walked, chosen or not: a particle two effects share (the shadow under both
        // markers in build 6967) takes a colour only when all its effects have that same colour,
        // otherwise recolouring it would change an effect left as in the game.
        var owners = new Dictionary<string, List<MouseEffect>>(StringComparer.Ordinal);
        foreach (MouseEffect effect in Enum.GetValues<MouseEffect>())
        {
            bool chosen = choice[effect] is not null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(Roots(effect));
            while (queue.TryDequeue(out string? particle))
            {
                if (!seen.Add(particle))
                {
                    continue;
                }

                if (!owners.TryGetValue(particle, out List<MouseEffect>? effects))
                {
                    owners[particle] = effects = [];
                }

                effects.Add(effect);
                if (!resources.TryGetValue(particle, out byte[]? resource))
                {
                    resources[particle] = resource = read(particle + "_c");
                }

                try
                {
                    if (resource is null)
                    {
                        throw new InvalidDataException("not in the game");
                    }

                    foreach (string child in ResourceReferences.Read(resource).Where(r => r.EndsWith(".vpcf", StringComparison.Ordinal)))
                    {
                        queue.Enqueue(child);
                    }
                }
                catch (InvalidDataException ex)
                {
                    Report(chosen, $"{particle}: {ex.Message}");
                }
            }
        }

        var files = new List<PackedFile>();
        Dictionary<MouseEffect, int> counts = choice.Chosen.ToDictionary(c => c.Key, _ => 0);
        foreach ((string particle, List<MouseEffect> effects) in owners)
        {
            if (effects.Select(e => choice[e]).Distinct().ToList() is not [Rgb color] || resources[particle] is not { } resource)
            {
                continue;
            }

            try
            {
                Kv3Document document = Kv3Binary.Read(CompiledResource.Data(resource));
                if (Paint(document.Root, color) > 0)
                {
                    files.Add(new PackedFile(particle + "_c", CompiledResource.WithData(resource, Kv3Binary.Write(document))));
                    effects.ForEach(e => counts[e]++);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            {
                // One particle the game changed in a way not foreseen stays as it is; the rest of the pack goes on.
                Report(true, $"{particle}: {ex.Message}");
            }
        }

        return new MouseRecolor(files, counts, problems);

        void Report(bool chosen, string problem)
        {
            if (chosen && !problems.Contains(problem))
            {
                problems.Add(problem);
            }
        }
    }

    /// <summary>
    /// Puts the colour into a particle and returns how many values changed. A remap from a control
    /// point into the colour gets the colour at both ends of its output, so whatever the game
    /// passes comes out as the colour. An operator that sets the colour from a control point reads
    /// the colour as a literal instead, at the point's scale. A colour written in the file keeps its
    /// brightness: black shadows stay black, dimmer layers stay dimmer. Textures are left alone:
    /// their gradients can be masks, and a colour there would cut channels out.
    /// </summary>
    internal static int Paint(Kv3Value value, Rgb color)
    {
        int changed = 0;
        if (value.Members is { } members)
        {
            if (IsColorRemap(value) && (value["m_vOutputMax"] ?? Kv3Value.Doubles(1, 1, 1)) is { Numbers.Length: 3 } max)
            {
                double[] output = [color.R / 255.0, color.G / 255.0, color.B / 255.0];
                Kv3Value min = max.Clone();
                max.SetNumbers(output);
                min.SetNumbers(output);
                value.Set("m_vOutputMax", max);
                value.Set("m_vOutputMin", min);
                changed++;
            }

            foreach (Kv3Member member in members)
            {
                if (member.Name == "m_vecTexturesInput")
                {
                    continue;
                }

                // A colour the game passes in a control point becomes the chosen one written in the
                // file; the input is not walked further, or its new literal would be painted again.
                // The point's scale goes into the literal, which has none: a layer at half keeps half.
                if (member.Name == "m_InputValue" && IsColorFromPoint(value) && member.Value["m_LiteralColor"] is { Numbers.Length: 3 } literal)
                {
                    double[] scale = member.Value["m_vCPValueScale"]?.Numbers is { Length: 3 } cpScale ? cpScale : [1, 1, 1];
                    member.Value.Set("m_nType", Kv3Value.String(member.Value["m_nType"]!.Flag, "PVEC_TYPE_LITERAL_COLOR"));
                    literal.SetNumbers([Scaled(color.R, scale[0]), Scaled(color.G, scale[1]), Scaled(color.B, scale[2])]);
                    changed++;
                    continue;
                }

                if (IsColor(value, member) && member.Value.Numbers is { Length: 3 or 4 } numbers)
                {
                    double brightness = Math.Max(numbers[0], Math.Max(numbers[1], numbers[2])) / 255;
                    double[] painted = [.. numbers];
                    painted[0] = Math.Round(color.R * brightness);
                    painted[1] = Math.Round(color.G * brightness);
                    painted[2] = Math.Round(color.B * brightness);
                    if (!painted.SequenceEqual(numbers))
                    {
                        member.Value.SetNumbers(painted);
                        changed++;
                    }
                }
                else
                {
                    changed += Paint(member.Value, color);
                }
            }
        }
        else if (value.Items is { } items && value.Numbers is null)
        {
            foreach (Kv3Value item in items)
            {
                changed += Paint(item, color);
            }
        }

        return changed;
    }

    // Only a remap that replaces the colour: one that scales or adds to it would mix the chosen colour with the painted literal.
    private static bool IsColorRemap(Kv3Value value) =>
        value["_class"]?.Text is "C_INIT_RemapCPtoVector" or "C_OP_RemapCPtoVector" or "C_INIT_RemapTransformToVector"
        && value["m_nFieldOutput"] is { } field
        && IsColorField(field)
        && IsReplace(value);

    /// <summary>
    /// An operator that sets the colour to a vector read from a control point. The tower range
    /// (build 6969) does it with C_OP_SetVec naming no field: its root calls point 4 the colour
    /// defined in code, so a SetVec without a field is taken to write the colour. An initializer
    /// counts only with the colour field named.
    /// </summary>
    private static bool IsColorFromPoint(Kv3Value value)
    {
        string? kind = value["_class"]?.Text;
        if (kind is not ("C_OP_SetVec" or "C_INIT_InitVec") || value["m_InputValue"]?["m_nType"]?.Text != "PVEC_TYPE_CP_VALUE")
        {
            return false;
        }

        bool color = value["m_nOutputField"] is { } field ? IsColorField(field) : kind == "C_OP_SetVec";
        return color && IsReplace(value);
    }

    private static double Scaled(byte channel, double scale) => Math.Clamp(Math.Round(channel * scale), 0, 255);

    private static bool IsColorField(Kv3Value field) => (field.IsNumber && field.Number == ColorField) || field.Text == "PARTICLE_ATTRIBUTE_TINT_RGB";

    private static bool IsReplace(Kv3Value value) =>
        value["m_nSetMethod"] is null or { Text: "PARTICLE_SET_REPLACE_VALUE" } or { IsNumber: true, Number: 0 };

    // A literal colour counts only where the vector input is set to it; other vector inputs keep an unused one.
    private static bool IsColor(Kv3Value owner, Kv3Member member) => member.Name switch
    {
        "m_ConstantColor" or "m_ColorMin" or "m_ColorMax" or "m_ColorFade" or "m_Color" or "m_TintColor" => true,
        "m_LiteralColor" => owner["m_nType"]?.Text == "PVEC_TYPE_LITERAL_COLOR",
        _ => false,
    };

    private static byte[] ReadEntry(Package package, PackageEntry entry)
    {
        package.ReadEntry(entry, out byte[] data, validateCrc: true);
        return data;
    }
}
