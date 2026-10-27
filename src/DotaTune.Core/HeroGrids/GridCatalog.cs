using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using DotaTune.Core.Diagnostics;

namespace DotaTune.Core.HeroGrids;

public enum CatalogOrigin
{
    /// <summary>The copy built into this version of DotaTune.</summary>
    BuiltIn,

    /// <summary>The copy downloaded last.</summary>
    Downloaded,
}

/// <summary>Ready-made layouts on offer and how old they are.</summary>
public sealed record GridCatalog(IReadOnlyList<HeroGridLayout> Layouts, CatalogOrigin Origin, DateTimeOffset Date);

/// <summary>Layouts out of hero grid files made elsewhere: egezenn's daily file, a D2PT download, a friend's file.</summary>
public static class GridLayouts
{
    /// <summary>A meta grid is a few kilobytes, an art grid of thousands of categories under a megabyte.</summary>
    public const int MaxFileBytes = 4 * 1024 * 1024;

    private const int MaxLayouts = 64;

    /// <summary>The valid layouts of the file in its order, the first of each name; broken ones are skipped.</summary>
    /// <exception cref="InvalidDataException">Not a hero grid file, or not one layout in it the game would show.</exception>
    public static IReadOnlyList<HeroGridLayout> Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaxFileBytes)
        {
            throw new InvalidDataException($"{bytes.Length} bytes is too large for a hero grid file");
        }

        List<HeroGridLayout> layouts =
        [
            .. GridFile.Parse(bytes).Entries
                .Select(e => e.Layout)
                .OfType<HeroGridLayout>()
                .Where(l => l.Categories.Count > 0)
                .DistinctBy(l => l.Name, StringComparer.Ordinal)
                .Take(MaxLayouts),
        ];
        return layouts.Count > 0 ? layouts : throw new InvalidDataException("the file has no hero grid layout the game would show");
    }

    /// <summary>The layouts of a hero grid file built into the program.</summary>
    internal static IReadOnlyList<HeroGridLayout> ReadResource(string name)
    {
        using Stream stream = typeof(GridLayouts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Resource {name} is missing from the build");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Read(copy.ToArray());
    }

    /// <summary>
    /// Fresh layouts, except that one cut down to a single category keeps its earlier full
    /// version: on about every other day egezenn's Spectral layout comes with "All Heroes" only.
    /// </summary>
    public static IReadOnlyList<HeroGridLayout> Merge(IReadOnlyList<HeroGridLayout> earlier, IReadOnlyList<HeroGridLayout> fresh)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(fresh);
        return
        [
            .. fresh.Select(layout => layout.Categories.Count < 2
                && earlier.FirstOrDefault(e => string.Equals(e.Name, layout.Name, StringComparison.Ordinal)) is { Categories.Count: >= 2 } full
                    ? full
                    : layout),
        ];
    }

    /// <summary>The layouts as a hero grid file on one line, as the cache keeps them.</summary>
    public static byte[] ToFile(IReadOnlyList<HeroGridLayout> layouts)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        return Encoding.UTF8.GetBytes("{\"version\":3,\"configs\":[" + string.Join(',', layouts.Select(l => l.Compact)) + "]}");
    }
}

/// <summary>
/// The layouts on offer: the last download when there is one, else the copy built into the
/// program, so the page works without the internet. Downloads are kept in one cache file.
/// </summary>
public sealed class GridCatalogStore(string cacheFile, Func<CancellationToken, Task<byte[]>>? download, FileLog log)
{
    /// <summary>When the built-in copy was released by egezenn/dota2-precompiled-grids.</summary>
    public static readonly DateTimeOffset BuiltInDate = new(2026, 10, 26, 2, 39, 25, TimeSpan.Zero);

    /// <summary>egezenn releases once a day; a page opened twice a day downloads at most twice.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    private const string BuiltInResource = "DotaTune.HeroGrids.precompiled-grids.json";

    /// <summary>Null in tests and the designer: no internet, the cache and the built-in copy only.</summary>
    public bool CanDownload => download is not null;

    public GridCatalog Current()
    {
        if (File.Exists(cacheFile))
        {
            try
            {
                return new GridCatalog(GridLayouts.Read(File.ReadAllBytes(cacheFile)), CatalogOrigin.Downloaded, File.GetLastWriteTimeUtc(cacheFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                log.Error($"Hero grid cache {cacheFile} is unreadable, the built-in layouts are offered", ex);
            }
        }

        return BuiltIn();
    }

    /// <summary>Nothing was downloaded yet, or the download is older than <see cref="MaxAge"/>.</summary>
    public bool IsStale(DateTimeOffset now) =>
        CanDownload && (!File.Exists(cacheFile) || now - File.GetLastWriteTimeUtc(cacheFile) > MaxAge);

    /// <summary>Downloads the layouts, keeps them in the cache and returns them.</summary>
    /// <exception cref="HttpRequestException">No source answered.</exception>
    /// <exception cref="InvalidDataException">The answer is not a hero grid file.</exception>
    /// <exception cref="IOException">The cache could not be written.</exception>
    public async Task<GridCatalog> RefreshAsync(CancellationToken cancel)
    {
        if (download is null)
        {
            throw new InvalidOperationException("Downloads are off");
        }

        byte[] bytes = await download(cancel).ConfigureAwait(false);
        IReadOnlyList<HeroGridLayout> layouts = GridLayouts.Merge(Current().Layouts, GridLayouts.Read(bytes));
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        string temp = cacheFile + ".tmp";
        await File.WriteAllBytesAsync(temp, GridLayouts.ToFile(layouts), cancel).ConfigureAwait(false);
        File.Move(temp, cacheFile, overwrite: true);
        log.Info($"Hero grid layouts downloaded: {string.Join(", ", layouts.Select(l => $"{l.Name} ({l.Categories.Count})"))}");
        return new GridCatalog(layouts, CatalogOrigin.Downloaded, File.GetLastWriteTimeUtc(cacheFile));
    }

    public static GridCatalog BuiltIn() => new(GridLayouts.ReadResource(BuiltInResource), CatalogOrigin.BuiltIn, BuiltInDate);
}

/// <summary>
/// Pictures drawn with category names, picked on gridstudio.me by hand: built into the
/// program, never downloaded, offered apart from the layouts for picking heroes.
/// </summary>
public static class ArtGrids
{
    private const string Resource = "DotaTune.HeroGrids.art-grids.json";

    public static IReadOnlyList<HeroGridLayout> BuiltIn() => GridLayouts.ReadResource(Resource);
}

/// <summary>egezenn/dota2-precompiled-grids (MIT): rebuilt every day from Stratz and Spectral statistics.</summary>
public static class GridDownload
{
    public static IReadOnlyList<Uri> Sources { get; } =
    [
        new("https://github.com/egezenn/dota2-precompiled-grids/releases/latest/download/hero_grid_config.json"),
        new("https://egezenn.github.io/dota2-precompiled-grids/hero_grid_config.json"),
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>The file from the first source that gives it.</summary>
    /// <exception cref="HttpRequestException">No source gave it; the inner exception lists why.</exception>
    public static async Task<byte[]> FromInternetAsync(string version, CancellationToken cancel)
    {
        var failures = new List<Exception>();
        foreach (Uri source in Sources)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DotaTune", version));
                using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    return await ReadLimitedAsync(body, cancel).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                || (ex is TaskCanceledException && !cancel.IsCancellationRequested))
            {
                // A timeout comes as a cancellation that nobody asked for; the next source is tried.
                failures.Add(new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"{source.Host}: {ex.Message}"), ex));
            }
        }

        throw new HttpRequestException(string.Join("; ", failures.Select(f => f.Message)), new AggregateException(failures));
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream body, CancellationToken cancel)
    {
        using var copy = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + read > GridLayouts.MaxFileBytes)
            {
                throw new InvalidDataException("the answer is larger than a hero grid file can be");
            }

            copy.Write(buffer, 0, read);
        }

        return copy.ToArray();
    }
}
