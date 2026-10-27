using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using DotaTune.Core.Diagnostics;

namespace DotaTune.Setup;

/// <summary>What the install folder holds compared with the version this installer carries.</summary>
public enum SetupCase
{
    /// <summary>Nothing installed yet.</summary>
    Fresh,

    /// <summary>An older version is installed.</summary>
    Update,

    /// <summary>The same version is installed.</summary>
    Same,

    /// <summary>A newer version is installed.</summary>
    Newer,

    /// <summary>The folder has files, but the installed version cannot be read.</summary>
    Unknown,
}

/// <param name="Present">The install folder has files, as Velopack checks before it asks to overwrite.</param>
/// <param name="Version">The installed version; null when there is none or it cannot be read.</param>
internal sealed record InstalledApp(bool Present, Version? Version);

internal static class Installation
{
    /// <summary>%LocalAppData%\DotaTune: Velopack installs into a folder named by the package id.</summary>
    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "DotaTune");

    /// <summary>The version this installer puts in place, the one of the whole build.</summary>
    public static Version ThisVersion() =>
        Parse(typeof(Installation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? throw new InvalidOperationException("The installer has no version of its own.");

    /// <summary>
    /// Reads the installed version from Velopack's manifest, current\sq.version, a nuspec file.
    /// A folder with files but without a readable manifest is still an installation to overwrite.
    /// </summary>
    public static InstalledApp Find(string root, FileLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        string manifest = Path.Combine(root, "current", "sq.version");
        try
        {
            if (!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any())
            {
                return new InstalledApp(false, null);
            }

            XElement? version = XDocument.Load(manifest).Descendants().FirstOrDefault(e => e.Name.LocalName == "version");
            Version? parsed = Parse(version?.Value);
            if (parsed is null)
            {
                log.Warning($"No version in {manifest}");
            }

            return new InstalledApp(true, parsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            // An unreadable folder is still offered for reinstalling; Velopack itself tells what it can do there.
            log.Warning($"Installed version not read from {manifest}: {ex.Message}");
            return new InstalledApp(true, null);
        }
    }

    public static SetupCase Decide(InstalledApp installed, Version current)
    {
        ArgumentNullException.ThrowIfNull(installed);
        return installed switch
        {
            { Present: false } => SetupCase.Fresh,
            { Version: null } => SetupCase.Unknown,
            { Version: var old } when old < current => SetupCase.Update,
            { Version: var old } when old > current => SetupCase.Newer,
            _ => SetupCase.Same,
        };
    }

    /// <summary>
    /// "0.1.0", "0.1.0+68277fc" or "0.2.0-beta" to 0.1.0 and 0.2.0, always three parts: Version
    /// counts a missing fourth part as lower than .0, so 0.1.0 and 0.1.0.0 would differ.
    /// </summary>
    internal static Version? Parse(string? text)
    {
        if (text is null)
        {
            return null;
        }

        int end = text.IndexOfAny(['+', '-']);
        if (!Version.TryParse(end < 0 ? text.Trim() : text[..end].Trim(), out Version? version))
        {
            return null;
        }

        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
