using ValvePak;

namespace DotaTune.Core.Game;

/// <summary>Single files of game\dota\pak01_dir.vpk, the archive the game's own files are in.</summary>
public static class GameArchive
{
    /// <summary>Reads one file from game\dota\pak01_dir.vpk. Only reads.</summary>
    /// <param name="path">The path inside the archive, with forward slashes, as the game names it.</param>
    /// <exception cref="FileNotFoundException">The game has no pak01_dir.vpk.</exception>
    /// <exception cref="InvalidDataException">The archive cannot be read or has no such file.</exception>
    public static byte[] ReadFile(string dotaFolder, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dotaFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string pak = Path.Combine(dotaFolder, "pak01_dir.vpk");
        if (!File.Exists(pak))
        {
            throw new FileNotFoundException("The game has no pak01_dir.vpk", pak);
        }

        try
        {
            using var package = new Package();
            package.Read(pak);
            PackageEntry entry = package.FindEntry(path) ?? throw new InvalidDataException($"{path} is not in {pak}");
            package.ReadEntry(entry, out byte[] data);
            return data;
        }
        catch (Exception ex) when (IsDamage(ex))
        {
            throw new InvalidDataException($"{pak} is not readable: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// How ValvePak 7.2 reports an unknown archive version or a damaged tree besides
    /// InvalidDataException and EndOfStreamException. FormatException is an entry without its
    /// 0xFFFF end, seen on damaged archives (lab\proby\2026-10-27-bityy-pak).
    /// </summary>
    public static bool IsDamage(Exception ex) =>
        ex is NotSupportedException or FormatException or ArgumentException or IndexOutOfRangeException or OverflowException;
}
