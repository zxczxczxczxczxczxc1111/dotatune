using System.Globalization;
using DotaTune.Core.Platform;
using Microsoft.Win32;
using ValveKeyValue;

namespace DotaTune.Core.Steam;

/// <summary>What the Steam client writes to HKCU\Software\Valve\Steam.</summary>
public interface ISteamRegistry
{
    /// <summary>Raw value, for example "d:/games/steam"; null when Steam was never installed.</summary>
    string? SteamPath { get; }

    /// <summary>Account id signed in right now; 0 when Steam is not running.</summary>
    uint ActiveUser { get; }

    /// <summary>Login name Steam signs in on start; null when it asks for a sign-in.</summary>
    string? AutoLoginUser { get; }
}

public sealed class WindowsSteamRegistry : ISteamRegistry
{
    private const string SteamKey = @"Software\Valve\Steam";

    public string? SteamPath
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SteamKey);
            return key?.GetValue("SteamPath") as string;
        }
    }

    public uint ActiveUser
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SteamKey + @"\ActiveProcess");
            return key?.GetValue("ActiveUser") is int value ? unchecked((uint)value) : 0;
        }
    }

    public string? AutoLoginUser
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SteamKey);
            return key?.GetValue("AutoLoginUser") as string;
        }
    }
}

public sealed record SteamAccount(uint AccountId, string AccountName, bool SignedInNow);

public sealed record SteamInfo(string Root, IReadOnlyList<string> Libraries, SteamAccount? Account);

public static class SteamLocator
{
    private const ulong SteamId64Base = 76561197960265728;

    /// <summary>Finds Steam, its libraries and the account Dota settings belong to. Null when Steam is not installed.</summary>
    public static SteamInfo? Locate(ISteamRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        string? raw = registry.SteamPath;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string root = NormalizePath(raw);
        if (!Directory.Exists(root))
        {
            return null;
        }

        return new SteamInfo(root, ReadLibraries(root), FindAccount(root, registry.ActiveUser, registry.AutoLoginUser));
    }

    /// <summary>"d:/games/steam" from the registry into a full Windows path with the real letter case.</summary>
    public static string NormalizePath(string raw) =>
        PathCase.Resolve(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(raw.Trim().Replace('/', '\\'))));

    /// <summary>Library folders from steamapps\libraryfolders.vdf; the Steam folder itself always comes first.</summary>
    public static IReadOnlyList<string> ReadLibraries(string steamRoot)
    {
        var libraries = new List<string> { steamRoot };
        string file = System.IO.Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file))
        {
            return libraries;
        }

        foreach (KeyValuePair<string, KVObject> entry in KeyValues.Load(file))
        {
            string? path = entry.Value.Child("path").Text();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string normalized = NormalizePath(path);
            if (!libraries.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                libraries.Add(normalized);
            }
        }

        return libraries;
    }

    /// <summary>
    /// The account signed in now. When the registry names none (Steam closed, and also a Steam just
    /// started again that has not written it), the one Steam signs in on start: AutoLoginUser from
    /// the registry, then from config\loginusers.vdf MostRecent in the older format, AutoLogin in the
    /// one Steam writes since October 2026, else the latest timestamp.
    /// </summary>
    public static SteamAccount? FindAccount(string steamRoot, uint activeUser, string? autoLoginUser = null)
    {
        string file = System.IO.Path.Combine(steamRoot, "config", "loginusers.vdf");
        KVObject? users = File.Exists(file) ? KeyValues.Load(file) : null;

        if (activeUser != 0)
        {
            return new SteamAccount(activeUser, NameOf(users, activeUser) ?? string.Empty, SignedInNow: true);
        }

        if (users is null)
        {
            return null;
        }

        SteamAccount? best = null;
        (int Mark, long Time) bestOrder = (0, 0);
        foreach (KeyValuePair<string, KVObject> user in users)
        {
            if (!ulong.TryParse(user.Key, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId)
                || steamId <= SteamId64Base || steamId - SteamId64Base > uint.MaxValue)
            {
                continue;
            }

            string name = user.Value.Child("AccountName").Text() ?? string.Empty;
            int mark = !string.IsNullOrEmpty(autoLoginUser) && string.Equals(name, autoLoginUser, StringComparison.OrdinalIgnoreCase) ? 3
                : IsOn(user.Value, "MostRecent") ? 2
                : IsOn(user.Value, "AutoLogin") ? 1
                : 0;
            long time = long.TryParse(user.Value.Child("Timestamp").Text(), NumberStyles.None, CultureInfo.InvariantCulture, out long stamp) ? stamp : 0;
            if ((mark, time) != (0, 0) && (mark > bestOrder.Mark || (mark == bestOrder.Mark && time > bestOrder.Time)))
            {
                best = new SteamAccount((uint)(steamId - SteamId64Base), name, SignedInNow: false);
                bestOrder = (mark, time);
            }
        }

        return best;
    }

    private static bool IsOn(KVObject user, string key) =>
        string.Equals(user.Child(key).Text(), "1", StringComparison.Ordinal);

    /// <summary>Login names of the accounts in config\loginusers.vdf by account id; empty when the file is missing.</summary>
    public static IReadOnlyDictionary<uint, string> AccountNames(string steamRoot)
    {
        string file = System.IO.Path.Combine(steamRoot, "config", "loginusers.vdf");
        var names = new Dictionary<uint, string>();
        if (!File.Exists(file))
        {
            return names;
        }

        foreach (KeyValuePair<string, KVObject> user in KeyValues.Load(file))
        {
            if (ulong.TryParse(user.Key, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId)
                && steamId > SteamId64Base && steamId - SteamId64Base <= uint.MaxValue)
            {
                names[(uint)(steamId - SteamId64Base)] = user.Value.Child("AccountName").Text() ?? string.Empty;
            }
        }

        return names;
    }

    private static string? NameOf(KVObject? users, uint accountId)
    {
        string steamId = (accountId + SteamId64Base).ToString(CultureInfo.InvariantCulture);
        return users?.Child(steamId)?.Child("AccountName").Text();
    }
}
