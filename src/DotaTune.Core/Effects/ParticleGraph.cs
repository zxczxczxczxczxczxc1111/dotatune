namespace DotaTune.Core.Effects;

/// <param name="Children">Particles this one starts, by source name.</param>
/// <param name="Draws">It references a material, texture or model, so it draws something itself.</param>
public sealed record ParticleInfo(IReadOnlyList<string> Children, bool Draws);

/// <summary>
/// The particles of the current game and who starts whom. Names are source names in lower
/// case, "particles/x.vpcf"; the compiled file in the game is that name plus "_c".
/// </summary>
public sealed class ParticleGraph
{
    private readonly Dictionary<string, List<string>> _parents = new(StringComparer.Ordinal);

    /// <param name="projectiles">Hero attack projectiles the game declares, with the ones cosmetics put in their place.</param>
    /// <param name="itemCreated">Particles an item starts as soon as it is worn (particle_create in items_game.txt).</param>
    public ParticleGraph(IReadOnlyDictionary<string, ParticleInfo> particles, IEnumerable<string> projectiles, IEnumerable<string>? itemCreated = null)
    {
        ArgumentNullException.ThrowIfNull(particles);
        ArgumentNullException.ThrowIfNull(projectiles);
        Particles = particles;
        Projectiles = projectiles.Where(particles.ContainsKey).ToHashSet(StringComparer.Ordinal);
        ItemCreated = (itemCreated ?? []).Where(particles.ContainsKey).ToHashSet(StringComparer.Ordinal);
        foreach ((string particle, ParticleInfo info) in particles)
        {
            foreach (string child in info.Children.Distinct(StringComparer.Ordinal))
            {
                if (!_parents.TryGetValue(child, out List<string>? parents))
                {
                    parents = [];
                    _parents[child] = parents;
                }

                parents.Add(particle);
            }
        }
    }

    public IReadOnlyDictionary<string, ParticleInfo> Particles { get; }

    public IReadOnlySet<string> Projectiles { get; }

    public IReadOnlySet<string> ItemCreated { get; }

    public IReadOnlyList<string> ParentsOf(string particle) =>
        _parents.TryGetValue(particle, out List<string>? parents) ? parents : [];

    public IReadOnlyList<string> ChildrenOf(string particle) =>
        Particles.TryGetValue(particle, out ParticleInfo? info) ? info.Children : [];

    /// <summary>"particles/x.vpcf" for "particles/x.vpcf_c" or "Particles/X.vpcf".</summary>
    public static string SourceName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string name = path.Replace('\\', '/').ToLowerInvariant();
        return name.EndsWith("_c", StringComparison.Ordinal) ? name[..^2] : name;
    }
}
