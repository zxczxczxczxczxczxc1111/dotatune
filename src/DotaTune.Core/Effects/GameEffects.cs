using System.Text;
using System.Text.RegularExpressions;
using DotaTune.Core.Game;
using ValvePak;

namespace DotaTune.Core.Effects;

/// <summary>
/// What the effect rules need from the installed game: its particles, its projectiles, Valve's
/// empty particle and the cleared fog haze (null when the game no longer has it as expected;
/// <paramref name="MistProblem"/> then says why when the files could not be read).
/// </summary>
public sealed partial record GameEffects(ParticleGraph Graph, byte[] Blank, PackedFile? Mist = null, string? MistProblem = null)
{
    /// <summary>Valve's own empty particle: a definition with nothing to draw.</summary>
    public const string BlankParticle = "particles/error/null.vpcf_c";

    private const string ItemsGame = "scripts/items/items_game.txt";

    /// <summary>Reads game\dota\pak01_dir.vpk. Only reads.</summary>
    /// <exception cref="InvalidDataException">The archive or a particle in it cannot be read: the rules would be guessing.</exception>
    public static GameEffects Read(string dotaFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dotaFolder);
        string pak = Path.Combine(dotaFolder, "pak01_dir.vpk");
        if (!File.Exists(pak))
        {
            throw new FileNotFoundException("The game has no pak01_dir.vpk", pak);
        }

        try
        {
            return ReadArchive(pak);
        }
        catch (Exception ex) when (GameArchive.IsDamage(ex))
        {
            throw new InvalidDataException($"{pak} is not readable: {ex.Message}", ex);
        }
    }

    private static GameEffects ReadArchive(string pak)
    {
        using var package = new Package();
        package.Read(pak);
        PackageEntry blankEntry = package.FindEntry(BlankParticle) ?? throw new InvalidDataException($"{BlankParticle} is not in {pak}");
        byte[] blank = ReadEntry(package, blankEntry);

        var particles = new Dictionary<string, ParticleInfo>(StringComparer.Ordinal);
        foreach (PackageEntry entry in Entries(package, "vpcf_c"))
        {
            IReadOnlyList<string> references;
            try
            {
                references = ResourceReferences.Read(ReadEntry(package, entry));
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"{entry.GetFullPath()} in {pak}: {ex.Message}", ex);
            }

            List<string> children = [.. references.Where(IsParticle).Select(ParticleGraph.SourceName)];
            particles[ParticleGraph.SourceName(entry.GetFullPath())] = new ParticleInfo(children, references.Count > children.Count);
        }

        // Heroes only: creeps, towers and summons declare projectiles in npc_units.txt and keep their look.
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (PackageEntry entry in Entries(package, "txt").Where(e => IsHeroScript(e.GetFullPath())))
        {
            foreach (Match match in ProjectileModel().Matches(Encoding.UTF8.GetString(ReadEntry(package, entry))))
            {
                declared.Add(ParticleGraph.SourceName(match.Groups["value"].Value));
            }
        }

        List<string> projectiles = [.. declared];
        List<string> created = [];
        if (package.FindEntry(ItemsGame) is { } items)
        {
            ItemParticles fromItems = ItemsGameParticles(ReadEntry(package, items), declared);
            projectiles.AddRange(fromItems.Replacements);
            created = fromItems.Created;
        }

        (PackedFile? mist, string? problem) = ReadMist(package);
        return new GameEffects(new ParticleGraph(particles, projectiles, created), blank, mist, problem);
    }

    /// <summary>
    /// The haze is one optional texture: when the game changed it the pack goes without it, and
    /// the build reports the group as empty with the reason, rather than the whole pack failing.
    /// </summary>
    private static (PackedFile? Mist, string? Problem) ReadMist(Package package)
    {
        if (package.FindEntry(FogMist.Material) is not { } material)
        {
            return (null, $"{FogMist.Material} is not in the archive");
        }

        try
        {
            PackedFile? mist = FogMist.Clear(ReadEntry(package, material), path => package.FindEntry(path) is { } entry ? ReadEntry(package, entry) : null);
            return (mist, mist is null ? $"{FogMist.Material} names no plain RGBA mist texture" : null);
        }
        catch (InvalidDataException ex)
        {
            return (null, $"{FogMist.Material} or its texture is unreadable: {ex.Message}");
        }
    }

    /// <summary>Pack files: the empty particle under the compiled name of every chosen particle, and the cleared haze.</summary>
    public Dictionary<string, byte[]> PackFiles(IReadOnlyDictionary<EffectGroup, IReadOnlyList<string>> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string particle in selection.Values.SelectMany(p => p))
        {
            files[particle + "_c"] = Blank;
        }

        if (selection.ContainsKey(EffectGroup.FogMist) && Mist is not null)
        {
            files[Mist.Path] = Mist.Bytes;
        }

        return files;
    }

    /// <summary>How many files of the pack a group puts in; the haze is one texture, not particles.</summary>
    public int FilesOf(EffectGroup group, IReadOnlyList<string> particles)
    {
        ArgumentNullException.ThrowIfNull(particles);
        return group == EffectGroup.FogMist ? (Mist is null ? 0 : 1) : particles.Count;
    }

    /// <param name="Replacements">Cosmetic projectiles that take the place of a declared one.</param>
    /// <param name="Created">Particles an item starts when worn (asset_modifier of type particle_create) and no item puts in another's place.</param>
    internal sealed record ItemParticles(List<string> Replacements, List<string> Created);

    /// <summary>
    /// Particles items_game.txt names. Cosmetic projectiles: an asset_modifier of type particle*
    /// whose asset is a declared projectile puts its modifier in that projectile's place. Worn
    /// effects: particle_create starts its modifier with the item. The file is about 50 MB, so
    /// it is scanned line by line and only the lines of interest are decoded.
    /// </summary>
    internal static ItemParticles ItemsGameParticles(ReadOnlySpan<byte> itemsGame, IReadOnlySet<string> declared)
    {
        var found = new List<string>();
        var created = new List<string>();
        var replacing = new HashSet<string>(StringComparer.Ordinal);
        string? type = null, asset = null, modifier = null;
        while (!itemsGame.IsEmpty)
        {
            int end = itemsGame.IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = (end < 0 ? itemsGame : itemsGame[..end]).Trim(" \t\r"u8);
            itemsGame = end < 0 ? [] : itemsGame[(end + 1)..];
            if (line.SequenceEqual("{"u8) || line.SequenceEqual("}"u8))
            {
                Flush();
            }
            else if (line.StartsWith("\"type\""u8) || line.StartsWith("\"asset\""u8) || line.StartsWith("\"modifier\""u8))
            {
                Match match = KeyValue().Match(Encoding.UTF8.GetString(line));
                if (match.Success)
                {
                    string value = match.Groups["value"].Value;
                    switch (match.Groups["key"].Value)
                    {
                        case "type": type = value; break;
                        case "asset": asset = value; break;
                        default: modifier = value; break;
                    }
                }
            }
        }

        Flush();

        // A worn effect that another item also puts in place of a particle the game starts itself
        // (Tiny's tree items, Terrorblade's blades) stands for that particle and stays.
        return new ItemParticles(found, [.. created.Where(c => !replacing.Contains(c)).Distinct(StringComparer.Ordinal)]);

        void Flush()
        {
            if (type is not null && type.StartsWith("particle", StringComparison.Ordinal)
                && asset is not null && modifier is not null && IsParticle(modifier) && declared.Contains(ParticleGraph.SourceName(asset)))
            {
                found.Add(ParticleGraph.SourceName(modifier));
            }

            if (type is not null && modifier is not null && IsParticle(modifier))
            {
                if (type == "particle_create")
                {
                    created.Add(ParticleGraph.SourceName(modifier));
                }
                else
                {
                    replacing.Add(ParticleGraph.SourceName(modifier));
                }
            }

            (type, asset, modifier) = (null, null, null);
        }
    }

    private static bool IsParticle(string name) => name.EndsWith(".vpcf", StringComparison.OrdinalIgnoreCase);

    private static bool IsHeroScript(string path) =>
        path.StartsWith("scripts/npc/heroes/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("scripts/npc/npc_heroes.txt", StringComparison.OrdinalIgnoreCase);

    private static List<PackageEntry> Entries(Package package, string extension) =>
        package.Entries?.GetValueOrDefault(extension) ?? [];

    private static byte[] ReadEntry(Package package, PackageEntry entry)
    {
        package.ReadEntry(entry, out byte[] data, validateCrc: true);
        return data;
    }

    [GeneratedRegex("\"ProjectileModel\"\\s+\"(?<value>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectileModel();

    [GeneratedRegex("^\"(?<key>[a-z]+)\"\\s+\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();
}
