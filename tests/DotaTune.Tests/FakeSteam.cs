using System.Globalization;
using System.Text;
using DotaTune.Core.Game;
using DotaTune.Core.Steam;
using ValvePak;

namespace DotaTune.Tests;

/// <summary>
/// A Steam install with Dota in a second library, laid out like the real one and written
/// the way Steam writes it: UTF-8 without BOM, LF, doubled backslashes in paths. Both
/// folders have Cyrillic letters and spaces in their paths.
/// </summary>
public sealed class FakeSteam : IDisposable, ISteamRegistry, IProcessProbe
{
    public const uint AccountId = 222222222;

    /// <summary>The other user of loginusers.vdf, "другой".</summary>
    public const uint OtherAccountId = 111111111;
    public const ulong SteamId64 = 76561198182487950;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly CyrillicTempFolder _temp = new();

    public FakeSteam()
    {
        SteamRoot = Path.Combine(_temp.Path, "Программы", "Steam");
        Library = Path.Combine(_temp.Path, "Игры", "Steam Library");
        GameRoot = Path.Combine(Library, "steamapps", "common", "dota 2 beta");

        Write(Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"{{Escape(SteamRoot)}}"
            		"label"		""
            		"apps"
            		{
            			"228980"		"161614178"
            		}
            	}
            	"1"
            	{
            		"path"		"{{Escape(Library)}}"
            		"label"		"Игры"
            		"apps"
            		{
            			"570"		"83420877561"
            		}
            	}
            }
            """);
        Write(Path.Combine(Library, "steamapps", "appmanifest_570.acf"), """
            "AppState"
            {
            	"appid"		"570"
            	"name"		"Dota 2"
            	"installdir"		"dota 2 beta"
            	"buildid"		"26185107"
            }
            """);
        Write(Path.Combine(GameRoot, "game", "dota", "steam.inf"), "ClientVersion=6967\nServerVersion=6967\nappID=570\n");
        GameFiles.WritePak(Pak01, GameFiles.Default());
        foreach (string language in new[] { "koreana", "lv", "russian", "schinese" })
        {
            Write(Path.Combine(GameRoot, "game", $"dota_{language}", "gameinfo.gi"), "\"GameInfo\" {}\n");
        }

        Directory.CreateDirectory(Path.Combine(GameRoot, "game", "dota_addons"));
        Write(Path.Combine(SteamRoot, "config", "loginusers.vdf"), $$"""
            "users"
            {
            	"76561198071376839"
            	{
            		"AccountName"		"другой"
            		"PersonaName"		"Второй игрок"
            		"MostRecent"		"0"
            	}
            	"{{SteamId64}}"
            	{
            		"AccountName"		"demo_player"
            		"PersonaName"		"Игрок «Тест»"
            		"MostRecent"		"1"
            	}
            }
            """);
        SetLaunchOptions(string.Empty);
        SetBoot("english");
    }

    /// <summary>
    /// loginusers.vdf as the Steam client writes it since October 2026: no MostRecent, AutoLogin on
    /// the account it signs in, a lower-case timestamp. Here the other account signed in later.
    /// </summary>
    public void UseCurrentLoginFormat(bool withAutoLogin = true) =>
        Write(Path.Combine(SteamRoot, "config", "loginusers.vdf"), $$"""
            "users"
            {
            	"76561198071376839"
            	{
            		"AccountName"		"другой"
            		"PersonaName"		"Второй игрок"
            		"RememberPassword"		"1"
            		"WantsOfflineMode"		"0"
            		"SkipOfflineModeWarning"		"0"
            		"AutoLogin"		"0"
            		"timestamp"		"{{(withAutoLogin ? 1793070141 : 1792946732)}}"
            	}
            	"{{SteamId64}}"
            	{
            		"AccountName"		"demo_player"
            		"PersonaName"		"Игрок «Тест»"
            		"RememberPassword"		"1"
            		"WantsOfflineMode"		"0"
            		"SkipOfflineModeWarning"		"0"
            		"AutoLogin"		"{{(withAutoLogin ? 1 : 0)}}"
            		"timestamp"		"{{(withAutoLogin ? 1792946732 : 1793070141)}}"
            	}
            }
            """);

    public string SteamRoot { get; }

    public string Library { get; }

    public string GameRoot { get; }

    /// <summary>game\dota\pak01_dir.vpk, filled from <see cref="GameFiles.Default"/>.</summary>
    public string Pak01 => Path.Combine(GameRoot, "game", "dota", "pak01_dir.vpk");

    /// <summary>Stand-in for %AppData%\DotaTune of a user with a Cyrillic name.</summary>
    public string DataRoot => Path.Combine(_temp.Path, "Users", "Иван", "AppData", "Roaming", "DotaTune");

    /// <summary>Registry value as Steam writes it: lower case with forward slashes.</summary>
    public string? SteamPath => SteamRoot.ToLowerInvariant().Replace('\\', '/');

    public uint ActiveUser { get; set; } = AccountId;

    public string? AutoLoginUser { get; set; }

    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRunning(string processName) => Running.Contains(processName);

    public GameState Read() => new GameStateReader(this, this).Read();

    /// <summary>Writes boot.vcfg with the given voice language; null removes the file.</summary>
    public void SetBoot(string? audioLanguage, bool withKey = true)
    {
        string file = BootConfig.PathIn(GameRoot);
        if (audioLanguage is null)
        {
            File.Delete(file);
            return;
        }

        string body = withKey ? $"\t\"AudioLanguage\"\t\t\"{audioLanguage}\"\n\t\"UILanguage\"\t\t\"english\"\n" : "\t\"Something\"\t\t\"1\"\n";
        Write(file, $"\"boot\"\n{{\n{body}}}\n");
    }

    public void SetLaunchOptions(string options, uint account = AccountId) =>
        Write(LaunchOptions.LocalConfigPath(SteamRoot, account), $$"""
            "UserLocalConfigStore"
            {
            	"friends"
            	{
            		"PersonaName"		"Игрок «Тест»"
            	}
            	"Software"
            	{
            		"Valve"
            		{
            			"Steam"
            			{
            				"Apps"
            				{
            					"7"
            					{
            						"LaunchOptions"		"-not-dota"
            					}
            					"570"
            					{
            						"LastPlayed"		"1793001750"
            						"LaunchOptions"		"{{options.Replace("\"", "\\\"", StringComparison.Ordinal)}}"
            					}
            				}
            			}
            		}
            	}
            }
            """);

    /// <summary>Puts a pak*_dir.vpk into a language folder, optionally with the DotaTune marker.</summary>
    public void AddPak(string language, int slot, bool withMarker)
    {
        string folder = Path.Combine(GameRoot, "game", $"dota_{language}");
        Directory.CreateDirectory(folder);
        using var package = new Package();
        package.AddFile("cfg/probe.cfg", Encoding.ASCII.GetBytes("echo probe\n"));
        if (withMarker)
        {
            package.AddFile(LanguageFolders.MarkerPath, Encoding.UTF8.GetBytes("{\"version\":\"0.1.0\"}"));
        }

        package.Write(Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"pak{slot:D2}_dir.vpk")));
    }

    /// <summary>userdata\&lt;account&gt;\570, where the game keeps the saved values of an account.</summary>
    public string AccountGameFolder(uint account = AccountId) =>
        Path.Combine(SteamRoot, "userdata", account.ToString(CultureInfo.InvariantCulture), "570");

    /// <summary>A convar file laid out as the game writes it: "config", "convars", tab-separated pairs.</summary>
    public string WriteConvars(string relativePath, IEnumerable<(string Name, string Value)> values, uint account = AccountId)
    {
        string file = Path.Combine(AccountGameFolder(account), relativePath);
        var text = new StringBuilder("\"config\"\n{\n\t\"convars\"\n\t{\n");
        foreach ((string name, string value) in values)
        {
            text.Append(CultureInfo.InvariantCulture, $"\t\t\"{name}\"\t\t\"{value}\"\n");
        }

        Write(file, text.Append("\t}\n}\n").ToString());
        return file;
    }

    /// <summary>A file in userdata\&lt;account&gt;\570 written byte for byte, line ends as given.</summary>
    public string WriteGameFile(uint account, string relativePath, string text)
    {
        string file = Path.Combine(AccountGameFolder(account), relativePath.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, Utf8NoBom.GetBytes(text));
        return file;
    }

    /// <summary>userdata\&lt;account&gt;\570\remotecache.vdf listing the hero grid file, as after the game saved a grid.</summary>
    public void SetGridSeen(uint account = AccountId) =>
        Write(Path.Combine(AccountGameFolder(account), "remotecache.vdf"), """
            "570"
            {
            	"ChangeNumber"		"0"
            	"cfg/hero_grid_config.json"
            	{
            		"root"		"0"
            		"size"		"2157"
            		"syncstate"		"3"
            	}
            }
            """);

    /// <summary>userdata\&lt;account&gt;\7\remote\sharedconfig.vdf with the Dota cloud switch.</summary>
    public void SetCloud(uint account, bool on) =>
        Write(Path.Combine(SteamRoot, "userdata", account.ToString(CultureInfo.InvariantCulture), "7", "remote", "sharedconfig.vdf"), $$"""
            "UserRoamingConfigStore"
            {
            	"Software"
            	{
            		"Valve"
            		{
            			"Steam"
            			{
            				"apps"
            				{
            					"570"
            					{
            						"cloudenabled"		"{{(on ? 1 : 0)}}"
            					}
            				}
            				"cloudenabled"		"1"
            			}
            		}
            	}
            }
            """);

    public void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings("\n"), Utf8NoBom);
    }

    public void Dispose() => _temp.Dispose();

    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
}
