using System.Globalization;
using System.Reflection;
using DotaTune.Core.Accept;
using DotaTune.Core.Config;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Effects;
using DotaTune.Core.Game;
using DotaTune.Core.HeroGrids;
using DotaTune.Core.Menu;
using DotaTune.Core.Mmr;
using DotaTune.Core.Pack;
using DotaTune.Core.Platform;
using DotaTune.Core.Reset;
using DotaTune.Core.Settings;
using DotaTune.Core.Steam;

namespace DotaTune;

/// <summary>Everything the windows share: paths, log, settings, the game state reader and the pack service.</summary>
internal sealed class AppServices
{
    private AppServices(
        AppPaths paths, FileLog log, Func<GameState> readGame, GameLauncher launcher, SteamRestart steamRestart, Func<CancellationToken, Task<byte[]>>? gridDownload)
    {
        SteamRestart = steamRestart;
        HeroGrids = new HeroGridService(new GridBackupStore(paths.HeroGridStateFile, paths.HeroGridFolder, log), log);
        GridCatalog = new GridCatalogStore(Path.Combine(paths.HeroGridFolder, "catalog.json"), gridDownload, log);
        Paths = paths;
        Log = log;
        Settings = new SettingsStore(paths.SettingsFile, log);
        ReadGame = readGame;
        Packs = new PackService(new InstallStateStore(paths.InstallStateFile, log), log);
        Autoexec = new AutoexecService(new AutoexecStateStore(paths.AutoexecStateFile, log), log);
        Launch = new LaunchService(new LaunchChangeStore(paths.LaunchStateFile, log), log);
        Transfer = new TransferService(new TransferBackupStore(paths.TransferStateFile, paths.TransferBackupRoot, log), log);
        Launcher = launcher;
        MenuPictures = new MenuPictures(paths.MenuPictureFolder, log);
        Mmr = new MmrService(new MmrHistoryStore(paths.MmrHistoryFile, log), Packs, log);
        Reset = new GameReset(Packs, Autoexec, Transfer, Launch, HeroGrids, log);
    }

    public AppPaths Paths { get; }

    public FileLog Log { get; }

    public SettingsStore Settings { get; }

    /// <summary>Reads Steam and the game; only reads, never writes.</summary>
    public Func<GameState> ReadGame { get; }

    public PackService Packs { get; }

    public AutoexecService Autoexec { get; }

    public LaunchService Launch { get; }

    public TransferService Transfer { get; }

    public GameLauncher Launcher { get; }

    /// <summary>Closes Steam for a launch options change and starts it again.</summary>
    public SteamRestart SteamRestart { get; }

    public HeroGridService HeroGrids { get; }

    /// <summary>Ready-made hero grid layouts: the last download or the built-in copy.</summary>
    public GridCatalogStore GridCatalog { get; }

    public MenuPictures MenuPictures { get; }

    /// <summary>MMR readings from the game and the data of the MMR line in the pack.</summary>
    public MmrService Mmr { get; }

    /// <summary>Takes back everything DotaTune changed in the game; the "Reset" page.</summary>
    public GameReset Reset { get; }

    public static string AppVersion =>
        typeof(AppServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <param name="readGame">Tests pass a fake game; null reads the real Steam.</param>
    /// <param name="launcher">Tests pass a launcher that starts nothing; null starts the game through Steam.</param>
    /// <param name="gridDownload">Downloads the hero grid layouts; null, as in tests and the designer, never goes online.</param>
    /// <param name="steamRestart">Tests pass a restart that closes nothing; null closes and starts the real Steam.</param>
    public static AppServices Create(
        AppPaths paths,
        Func<GameState>? readGame = null,
        Func<FileLog, GameLauncher>? launcher = null,
        Func<CancellationToken, Task<byte[]>>? gridDownload = null,
        Func<FileLog, SteamRestart>? steamRestart = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var processes = new SystemProcessProbe();
        var reader = new GameStateReader(new WindowsSteamRegistry(), processes);
        var log = new FileLog(paths.LogDirectory);
        return new AppServices(
            paths,
            log,
            readGame ?? reader.Read,
            launcher?.Invoke(log) ?? new GameLauncher(new SteamGameStarter(), processes, log),
            steamRestart?.Invoke(log) ?? new SteamRestart(new SteamGameStarter(), processes, log),
            gridDownload);
    }

    /// <summary>
    /// The pack of this version: the marker plus Valve's empty particle under every effect of the
    /// groups chosen in the settings, picked from the current game, the mouse effects in their
    /// chosen colours and the chosen menu background.
    /// </summary>
    /// <exception cref="InvalidDataException">The game archive or a particle in it cannot be read.</exception>
    public BuiltPack BuildPack(GameState game)
    {
        ArgumentNullException.ThrowIfNull(game);
        AppSettings settings = Settings.Load();
        IReadOnlyList<EffectGroup> groups = settings.EffectGroups ?? EffectRules.All;
        Dictionary<string, byte[]> files = [];
        Dictionary<EffectGroup, int> effects = [];
        if (groups.Count > 0 && game.Dota is { } dota)
        {
            GameEffects fromGame = GameEffects.Read(dota.DotaFolder);
            IReadOnlyDictionary<EffectGroup, IReadOnlyList<string>> selection = EffectRules.Select(fromGame.Graph, groups);
            files = fromGame.PackFiles(selection);
            effects = selection.ToDictionary(s => s.Key, s => fromGame.FilesOf(s.Key, s.Value));
            Log.Info($"Blank effects for the pack: {string.Join(", ", effects.Select(e => $"{e.Key} {e.Value}"))}");
            foreach (EffectGroup empty in effects.Where(e => e.Value == 0).Select(e => e.Key))
            {
                // In build 6967 every particle group finds dozens of files or more and the haze its
                // one texture; none means the game changed its layout.
                Log.Warning($"Effect group {empty} found nothing in build {dota.Build}, its rules may need an update");
            }

            if (effects.ContainsKey(EffectGroup.FogMist) && fromGame.MistProblem is { } problem)
            {
                Log.Warning($"Fog haze stays in the game: {problem}");
            }
        }

        MouseColorChoice? mouse = null, taken = null;
        List<MouseEffect>? notTaken = null;
        if (settings.MouseColors is { IsEmpty: false } chosen && game.Dota is { } install)
        {
            MouseRecolor recolor = MouseEffects.Read(install.DotaFolder, chosen);
            foreach (PackedFile file in recolor.Files)
            {
                // The recolour wins over a blank: a marker the player chose a colour for has to stay visible.
                if (files.ContainsKey(file.Path))
                {
                    Log.Warning($"{file.Path} is both a blank effect and a recoloured mouse effect, the recolour goes in");
                }

                files[file.Path] = file.Bytes;
            }

            // An effect that got no file is not in the pack; the page tells the player so.
            notTaken = [.. recolor.Counts.Where(c => c.Value == 0).Select(c => c.Key)];
            taken = notTaken.Aggregate(chosen, (choice, effect) => choice.With(effect, null));
            mouse = chosen;
            Log.Info($"Recoloured mouse effects for the pack: {string.Join(", ", recolor.Counts.Select(c => $"{c.Key} {c.Value}"))}");
            foreach (string problem in recolor.Problems)
            {
                Log.Warning($"Mouse effect stays as in the game: {problem}");
            }
        }

        string? menu = game.Dota is { } forMenu ? AddMenuBackground(settings.MenuBackground, forMenu, files) : null;
        bool mmr = settings.MmrLine && game.Dota is { } forMmr && AddMmr(game, forMmr, files);
        int? accept = settings.AutoAccept ? AutoAccept.Delay(settings.AutoAcceptDelay) : null;
        if (accept is { } delay && game.Dota is { } forAccept && AcceptScript(forAccept, delay) is { } script)
        {
            files[AutoAccept.ScriptPath] = script;
        }
        else
        {
            // The window stays as the game has it.
            accept = null;
        }

        byte[] bytes = PackBuilder.Build(
            new PackMarker(PackBuilder.AppName, AppVersion, game.Dota?.Build, DateTimeOffset.Now, effects, menu, taken is { IsEmpty: false } ? taken : null, mmr),
            files);
        return new BuiltPack(bytes, effects, menu, mouse, notTaken is { Count: > 0 } ? notTaken : null, mmr, accept);
    }

    /// <summary>
    /// The script of the "match found" window with the accept lines for this delay; null with a log
    /// line when the game's script cannot take them.
    /// </summary>
    private byte[]? AcceptScript(DotaInstall dota, int delay)
    {
        try
        {
            byte[] script = AutoAccept.ReadGameScript(dota.DotaFolder);
            if (AutoAccept.Problem(script) is { } problem)
            {
                Log.Warning($"Auto accept does not fit build {dota.Build}: {problem}");
                return null;
            }

            Log.Info($"Auto accept script for build {dota.Build}: {delay} s");
            return AutoAccept.PackFile(script, delay);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warning($"Auto accept script of build {dota.Build} is not readable: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Puts another delay into a pack that already accepts matches, without building the pack
    /// again. False when there is no such pack, it is not ready for this build, Dota runs or the
    /// script cannot be made; the reason is in the log.
    /// </summary>
    /// <exception cref="InvalidDataException">install.json is there but cannot be read.</exception>
    public bool ReplaceAutoAccept(int delay)
    {
        GameState game = ReadGame();
        if (Packs.Current is not { AutoAccept: not null } || game.Dota is not { } dota)
        {
            Log.Info("Auto accept delay not replaced: no pack that accepts matches");
            return false;
        }

        // The rest of a pack from another build would stay stale; such a pack is built again.
        PackHealth health = Packs.Check(game);
        if (health != PackHealth.Ready)
        {
            Log.Info($"Auto accept delay not replaced: the pack is {health}");
            return false;
        }

        return AcceptScript(dota, delay) is { } script
            && Packs.ReplaceFiles(game, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [AutoAccept.ScriptPath] = script }, state => state with { AutoAccept = delay });
    }

    /// <summary>
    /// Adds the MMR line with the newest reading of every account, after the readings of the
    /// last session are taken in. False with a log line when the game's root menu layout is not
    /// the one the line is made from: the pack then leaves the menu alone.
    /// </summary>
    private bool AddMmr(GameState game, DotaInstall dota, Dictionary<string, byte[]> files)
    {
        try
        {
            if (MmrMenu.Problem(MmrMenu.ReadGameBase(dota.DotaFolder)) is { } problem)
            {
                Log.Warning($"MMR line is left out of the pack of build {dota.Build}: {problem}");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warning($"MMR line is left out of the pack: {ex.Message}");
            return false;
        }

        IReadOnlyList<MmrReading> latest;
        try
        {
            Mmr.Collect(game);
            latest = MmrHistoryStore.Latest(Mmr.History.Load());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The line still reads the number in the profile; only the change waits for a second reading.
            Log.Warning($"MMR history is not readable, the line goes in without it: {ex.Message}");
            latest = [];
        }

        foreach ((string path, byte[] data) in MmrMenu.PackFiles(latest, MmrLanguage))
        {
            files[path] = data;
        }

        Log.Info($"MMR line for the pack: readings of {latest.Count} accounts");
        return true;
    }

    /// <summary>
    /// Takes the MMR readings of the last game session in and puts the newest into the installed
    /// pack; at start, on the MMR page and before Play. A failure only goes to the log: the line in
    /// the game then counts the change from the reading before.
    /// </summary>
    /// <param name="read">The game as just read; null reads it here.</param>
    public void RefreshMmr(GameState? read = null)
    {
        try
        {
            if (!Settings.Load().MmrLine && Packs.Current is not { Mmr: true })
            {
                return;
            }

            GameState game = read ?? ReadGame();
            Mmr.Collect(game);
            Mmr.RefreshPack(game, MmrLanguage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error("MMR readings were not taken in", ex);
        }
    }

    /// <summary>The words of the MMR line in the game follow the language of DotaTune.</summary>
    private static string MmrLanguage =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ru", StringComparison.Ordinal) ? "ru" : "en";

    /// <summary>
    /// Adds the files of the chosen menu background to the pack and returns the id that went in;
    /// null for the game's own. A background that cannot be made is logged and left out, so the
    /// rest of the pack still goes in and the page shows that the background is missing.
    /// </summary>
    private string? AddMenuBackground(string? choice, DotaInstall dota, Dictionary<string, byte[]> files)
    {
        MenuBackgroundKind kind = MenuPictures.KindOf(choice);
        if (choice is null || kind == MenuBackgroundKind.Game)
        {
            return null;
        }

        try
        {
            GameMenuBackground menu = GameMenuBackground.Read(dota.DotaFolder);
            Dictionary<string, byte[]> added = menu.PackFiles(kind, kind == MenuBackgroundKind.Picture ? MenuPictures.Png(choice) : null);
            foreach ((string path, byte[] data) in added)
            {
                files[path] = data;
            }

            Log.Info($"Menu background {choice}: {added.Count} files over {menu.Layouts.Count} background layouts of build {dota.Build}");
            return choice;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Log.Warning($"Menu background {choice} is left out of the pack: {ex.Message}");
            return null;
        }
    }

    /// <summary>Builds the pack from the current game and installs it in place of an earlier one; null when it went in.</summary>
    /// <exception cref="InvalidDataException">The game archive or a particle in it cannot be read.</exception>
    public InstallRefusal? InstallPack()
    {
        // Building reads every particle of the game, so a pack that would be refused is not built.
        GameState game = ReadGame();
        InstallRefusal? refusal = PackService.RefusalFor(game);
        if (refusal is not null)
        {
            return refusal;
        }

        BuiltPack pack = BuildPack(game);
        InstallRefusal? installed = Packs.Install(
            ReadGame, pack.Bytes, pack.Effects, pack.MenuBackground, pack.MouseColors, pack.MouseNotTaken, pack.Mmr, pack.AutoAccept).Refusal;
        if (installed is null && pack.Mmr)
        {
            // A reading taken in while the pack was built went into the pack it replaced.
            RefreshMmr();
        }

        return installed;
    }

    /// <summary>
    /// Uninstall hook: the same reset as the "Reset" page except the settings copied between
    /// accounts. Velopack kills the process after 30 seconds and shows nothing, so every failure
    /// goes to the log.
    /// </summary>
    public void RemoveFromGame()
    {
        GameState game;
        try
        {
            game = ReadGame();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error("Could not read the game during uninstall, nothing is taken out", ex);
            return;
        }

        if (game.DotaRunning)
        {
            Log.Error("Dota is running during uninstall, the pack and the autoexec lines stay in the game");
            return;
        }

        // Settings copied between accounts stay: they are the player's own, only carried over.
        foreach (ResetStep step in Reset.Run(ReadGame, ResetPart.Transfers).Where(s => s.Problem is not null))
        {
            Log.Error($"{step.Part} stays in the game during uninstall: {step.Problem} {step.Detail}".TrimEnd());
        }
    }
}
