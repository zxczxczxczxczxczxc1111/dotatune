namespace DotaTune.Core.Effects;

/// <summary>
/// Groups of effects DotaTune replaces with Valve's empty particle (docs: dota-mody-razbor, step 4).
/// The order is the order on the page; settings and the pack marker keep groups by name.
/// </summary>
public enum EffectGroup
{
    /// <summary>Weather from cosmetics and cl_weather: rain, snow, moonlight, falling leaves.</summary>
    Weather,

    /// <summary>Life on the map: fireflies, butterflies, birds, torches, candles, lava, river flow, waterfalls, terrain ambience.</summary>
    MapAmbience,

    /// <summary>Idle glow of buildings: barracks, statues, the fountain, base gates, tower lamps; destruction, shrines and portals stay.</summary>
    BuildingAmbience,

    /// <summary>The drifting haze over the fog of war: a texture, not a particle (<see cref="FogMist"/>).</summary>
    FogMist,

    /// <summary>Event cosmetics: emblems, event hat ambience, kill banners, fireworks, loadout previews.</summary>
    EventCosmetics,

    /// <summary>What hero items and couriers start as soon as they are worn: sparks, smoke, glow; spell effects of items stay.</summary>
    CosmeticAmbience,

    Sprays,

    /// <summary>Effects of cosmetic wards; the default ward keeps its look.</summary>
    CosmeticWards,

    /// <summary>Flashes, trails and impacts of ranged hero attacks; the projectile itself stays.</summary>
    AttackFlashes,
}

/// <summary>
/// Picks the particles of each group from the current game by rules, not by a fixed list, so
/// a patch that adds or renames files is followed. A particle is left alone when an effect
/// that stays still starts it: an ability that shares a flash with the attack keeps it.
/// </summary>
public static class EffectRules
{
    private static readonly string[] EventCosmeticWords = ["emblem", "hat_ambient", "killbanner", "loadout", "fireworks", "effigy"];

    // Seen by enemies: an event teleport or blink must stay visible.
    private static readonly string[] EventActionWords = ["teleport", "blink"];

    private static readonly string[] FlashWords =
        ["explo", "impact", "hit", "launch", "flash", "trail", "spark", "ember", "smoke", "dust", "burst", "streak", "debris", "splash", "puff", "endcap"];

    // The projectile body: kept even under a flash-like name ("explosion_core").
    private static readonly string[] BodyWords =
        ["core", "main", "model", "proj", "ball", "orb", "arrow", "spear", "bullet", "head", "body", "sphere", "skull", "dagger", "knife", "axe", "bolt", "missile", "rocket"];

    private static readonly string[] BuildingFolders = ["particles/radiant_fx/", "particles/dire_fx/", "particles/base_static/", "particles/addons_gameplay/"];

    // Idle decoration without "amb" in the name.
    private static readonly string[] BuildingDecor = ["fountain_", "radiant_fountain", "dire_fountain", "lamp_flame", "tower_good_tintable", "tower_bad_tintable"];

    // Tell something about the game: a building falls or is hit, the shrine, the recall stone, the twin gates, runes, wards.
    private static readonly string[] BuildingEventWords =
        ["dest", "disinteg", "hit", "dmg", "damage", "death", "shrine", "recall", "portal", "deferred", "spawn", "respawn", "attack", "glyph", "tp", "teleport", "rune", "courier", "neutral", "ward", "creep"];

    // Cosmetic folders only: a hero's default items and the wards also start particles this way.
    private static readonly string[] CosmeticFolders = ["particles/econ/items/", "particles/econ/courier/", "particles/econ/treasures/"];

    public static IReadOnlyList<EffectGroup> All { get; } = Enum.GetValues<EffectGroup>();

    /// <returns>Source names per requested group, sorted; a particle that fits two groups goes to the first.</returns>
    public static IReadOnlyDictionary<EffectGroup, IReadOnlyList<string>> Select(ParticleGraph graph, IEnumerable<EffectGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(groups);
        List<EffectGroup> wanted = [.. groups.Distinct().Order()];
        var chosen = new Dictionary<string, EffectGroup>(StringComparer.Ordinal);
        IReadOnlySet<string> attackTree = new HashSet<string>(StringComparer.Ordinal);
        foreach (EffectGroup group in wanted)
        {
            IEnumerable<string> picked;
            if (group == EffectGroup.AttackFlashes)
            {
                (picked, attackTree) = AttackFlashes(graph);
            }
            else if (group == EffectGroup.CosmeticAmbience)
            {
                // Only the particle the item starts: everything under it goes quiet with it, and a
                // child that a spell of the item also uses keeps playing there.
                picked = graph.ItemCreated.Where(p => CosmeticFolders.Any(f => p.StartsWith(f, StringComparison.Ordinal)));
            }
            else
            {
                picked = graph.Particles.Keys.Where(p => Fits(group, p));
            }

            foreach (string particle in picked.Where(p => !graph.Projectiles.Contains(p) && IsAscii(p)))
            {
                chosen.TryAdd(particle, group);
            }
        }

        DropWhatStaysInUse(graph, chosen, attackTree);
        return wanted.ToDictionary(
            g => g,
            g => (IReadOnlyList<string>)[.. chosen.Where(kv => kv.Value == g).Select(kv => kv.Key).Order(StringComparer.Ordinal)]);
    }

    private static bool Fits(EffectGroup group, string particle)
    {
        string name = particle[(particle.LastIndexOf('/') + 1)..];
        return group switch
        {
            EffectGroup.Weather =>
                (particle.StartsWith("particles/rain_fx/", StringComparison.Ordinal) && !IsTerrain(name)
                    && (name.StartsWith("econ_", StringComparison.Ordinal) || name.StartsWith("rain", StringComparison.Ordinal) || name.StartsWith("snow", StringComparison.Ordinal)))
                || particle.StartsWith("particles/winter_fx/weather_", StringComparison.Ordinal),
            EffectGroup.MapAmbience =>
                (particle.StartsWith("particles/world_environmental_fx/", StringComparison.Ordinal)
                    && !name.StartsWith("rune_", StringComparison.Ordinal) && !name.Contains("creep", StringComparison.Ordinal))
                || particle.StartsWith("particles/world_creature_fx/", StringComparison.Ordinal)
                || particle.StartsWith("particles/terrain_fx/", StringComparison.Ordinal)
                || (particle.StartsWith("particles/rain_fx/", StringComparison.Ordinal) && IsTerrain(name))
                || particle.StartsWith("particles/dire_fx/dire_lava", StringComparison.Ordinal)
                || particle.StartsWith("particles/dire_fx/incense", StringComparison.Ordinal)
                || IsTerrainAsset(particle),
            EffectGroup.BuildingAmbience =>
                BuildingFolders.Any(f => particle.StartsWith(f, StringComparison.Ordinal))
                && !Words(name).Any(w => BuildingEventWords.Any(e => w.StartsWith(e, StringComparison.Ordinal)))
                && (Words(name).Any(w => w.StartsWith("amb", StringComparison.Ordinal)) || BuildingDecor.Any(d => name.StartsWith(d, StringComparison.Ordinal))),
            EffectGroup.EventCosmetics =>
                particle.StartsWith("particles/econ/events/", StringComparison.Ordinal)
                && EventCosmeticWords.Any(w => name.Contains(w, StringComparison.Ordinal))
                && !EventActionWords.Any(w => name.Contains(w, StringComparison.Ordinal))
                && !Words(name).Contains("tp", StringComparer.Ordinal),
            EffectGroup.Sprays => particle.StartsWith("particles/sprays/", StringComparison.Ordinal),
            EffectGroup.CosmeticWards => particle.StartsWith("particles/econ/wards/", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>Ambience of custom terrains in rain_fx: autumn_terrain, desert_sand_drift and the like.</summary>
    private static bool IsTerrain(string name) =>
        name.Contains("terrain", StringComparison.Ordinal) || name.StartsWith("desert_sand", StringComparison.Ordinal);

    /// <summary>maps/cavern_assets/particles/... and the like; maps/backgrounds is the main menu and stays.</summary>
    private static bool IsTerrainAsset(string particle)
    {
        string[] parts = particle.Split('/');
        return parts.Length > 3 && parts[0] == "maps" && parts[1].EndsWith("_assets", StringComparison.Ordinal) && parts[2] == "particles";
    }

    /// <summary>
    /// Roots are the projectiles the game declares. A root that draws nothing itself is skipped:
    /// its children are the projectile. Under the rest, children named as flashes, trails and
    /// impacts become blanks, children that look like the projectile body stay.
    /// </summary>
    private static (List<string> Flashes, HashSet<string> Tree) AttackFlashes(ParticleGraph graph)
    {
        var flashes = new List<string>();
        var tree = new HashSet<string>(StringComparer.Ordinal);
        foreach (string root in graph.Projectiles.Where(r => graph.Particles[r].Draws))
        {
            tree.Add(root);
            var pending = new Stack<string>(graph.ChildrenOf(root));
            while (pending.Count > 0)
            {
                string child = pending.Pop();
                if (!tree.Add(child))
                {
                    continue;
                }

                if (!graph.Projectiles.Contains(child) && IsFlash(child))
                {
                    flashes.Add(child);
                }

                foreach (string grandchild in graph.ChildrenOf(child))
                {
                    pending.Push(grandchild);
                }
            }
        }

        return (flashes, OnlyUnderProjectiles(graph, tree));
    }

    /// <summary>
    /// The part of the attack trees that nothing outside them starts, so a blank under it touches
    /// only attacks. A projectile that an ability or a taunt also starts is not part of it.
    /// </summary>
    private static HashSet<string> OnlyUnderProjectiles(ParticleGraph graph, HashSet<string> tree)
    {
        var pure = new HashSet<string>(tree, StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (string particle in pure.ToList())
            {
                if (graph.ParentsOf(particle).Any(parent => !pure.Contains(parent)))
                {
                    pure.Remove(particle);
                    changed = true;
                }
            }
        }
        while (changed);

        return pure;
    }

    private static bool IsFlash(string particle)
    {
        string[] words = Words(particle[(particle.LastIndexOf('/') + 1)..]);
        return words.Any(w => FlashWords.Any(f => w.StartsWith(f, StringComparison.Ordinal)))
            && !words.Any(w => BodyWords.Any(b => w.StartsWith(b, StringComparison.Ordinal)));
    }

    /// <summary>Removes, until nothing changes, every chosen particle that an effect which stays still starts.</summary>
    private static void DropWhatStaysInUse(ParticleGraph graph, Dictionary<string, EffectGroup> chosen, IReadOnlySet<string> attackTree)
    {
        bool changed;
        do
        {
            changed = false;
            foreach ((string particle, EffectGroup group) in chosen.ToList())
            {
                bool usedByKept = graph.ParentsOf(particle).Any(parent =>
                    !chosen.ContainsKey(parent) && !(group == EffectGroup.AttackFlashes && attackTree.Contains(parent)));
                if (usedByKept)
                {
                    chosen.Remove(particle);
                    changed = true;
                }
            }
        }
        while (changed);
    }

    private static string[] Words(string name)
    {
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        return (dot < 0 ? name : name[..dot]).Split('_', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsAscii(string path) => path.All(c => c < 0x80);
}
