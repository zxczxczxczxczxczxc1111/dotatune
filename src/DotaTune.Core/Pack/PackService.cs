using DotaTune.Core.Diagnostics;
using DotaTune.Core.Effects;
using DotaTune.Core.Game;

namespace DotaTune.Core.Pack;

public enum InstallRefusal
{
    DotaRunning,
    NoGame,
    NothingToDo,
    NoFreeSlot,
    WrittenFileDiffers,
}

public sealed record InstallResult(InstallState? State, InstallRefusal? Refusal)
{
    public bool Installed => State is not null;
}

public enum PackHealth
{
    /// <summary>DotaTune has not installed anything.</summary>
    NotInstalled,

    /// <summary>The pack is in place, unchanged, and the game loads its folder.</summary>
    Ready,

    /// <summary>The pack file is gone, usually after "Verify integrity of game files".</summary>
    Missing,

    /// <summary>The file in our slot is not the one DotaTune wrote.</summary>
    Changed,

    /// <summary>The voice language or -language moved the game to another folder; the pack is not loaded.</summary>
    FolderNotLoaded,

    /// <summary>A new game build: the pack has to be rebuilt from the current game.</summary>
    OtherBuild,
}

/// <summary>
/// Puts the DotaTune pack into the language folder the game loads, checks it and takes it
/// out again, following docs: the voice language is changed only for English voices, and
/// only DotaTune's own files are ever removed.
/// </summary>
public sealed class PackService(InstallStateStore store, FileLog log)
{
    public const int DefaultSlot = 40;
    public const int LastSlot = 63;

    // Install, Remove and ReplaceFiles run off the UI thread from different pages and the start
    // of the app: one at a time, or one could write the state of a pack the other just took out.
    private readonly Lock _gate = new();

    /// <summary>What DotaTune installed, or null.</summary>
    public InstallState? Current => store.Load();

    /// <summary>Why a pack cannot go into this game right now, before it is built; null when it can.</summary>
    public static InstallRefusal? RefusalFor(GameState game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.DotaRunning)
        {
            return InstallRefusal.DotaRunning;
        }

        if (game.Dota is null || game.Decision is null)
        {
            return InstallRefusal.NoGame;
        }

        return game.Decision.TargetFolder is not null && IsInstallAction(game.Decision.Action) ? null : InstallRefusal.NothingToDo;
    }

    /// <param name="readGame">Reads the game state; called again after an old pack is taken out,
    /// because taking it out may return the voice language and so change the target folder.</param>
    /// <param name="effects">What the pack replaces, kept in the install state for the pages.</param>
    /// <param name="menuBackground">Id of the menu background in the pack, kept in the install state for its page.</param>
    /// <param name="mouseColors">The mouse colours the pack was built for, kept the same way.</param>
    /// <param name="mouseNotTaken">Those of them the pack could not recolour.</param>
    /// <param name="mmr">The pack carries the MMR line.</param>
    /// <param name="autoAccept">Seconds after which the pack accepts a found match; null when it does not.</param>
    public InstallResult Install(
        Func<GameState> readGame, byte[] pak, IReadOnlyDictionary<EffectGroup, int>? effects = null, string? menuBackground = null,
        MouseColorChoice? mouseColors = null, IReadOnlyList<MouseEffect>? mouseNotTaken = null, bool mmr = false, int? autoAccept = null)
    {
        ArgumentNullException.ThrowIfNull(readGame);
        ArgumentNullException.ThrowIfNull(pak);
        lock (_gate)
        {
            return InstallLocked(readGame, pak, effects, menuBackground, mouseColors, mouseNotTaken, mmr, autoAccept);
        }
    }

    private InstallResult InstallLocked(
        Func<GameState> readGame, byte[] pak, IReadOnlyDictionary<EffectGroup, int>? effects, string? menuBackground,
        MouseColorChoice? mouseColors, IReadOnlyList<MouseEffect>? mouseNotTaken, bool mmr, int? autoAccept)
    {
        GameState game = readGame();
        if (game.DotaRunning)
        {
            return Refuse(InstallRefusal.DotaRunning);
        }

        InstallState? previous = store.Load();
        if (previous is not null)
        {
            Remove(game);
            game = readGame();
        }

        if (game.Dota is null || game.Decision is null)
        {
            return Refuse(InstallRefusal.NoGame);
        }

        PackDecision decision = game.Decision;
        if (decision.TargetFolder is null || !IsInstallAction(decision.Action))
        {
            return Refuse(InstallRefusal.NothingToDo);
        }

        string folder = Path.Combine(game.Dota.GameRoot, "game", $"dota_{decision.TargetFolder}");
        int? slot = FreeSlot(folder);
        if (slot is null)
        {
            return Refuse(InstallRefusal.NoFreeSlot);
        }

        string target = Path.Combine(folder, InstallState.PakFileName(slot.Value));
        string temp = target + ".dotatune.tmp";
        File.WriteAllBytes(temp, pak);
        File.Move(temp, target, overwrite: true);
        string sha1 = PackBuilder.Sha1(pak);
        if (!string.Equals(PackBuilder.Sha1OfFile(target), sha1, StringComparison.Ordinal))
        {
            File.Delete(target);
            return Refuse(InstallRefusal.WrittenFileDiffers);
        }

        string? previousVoice = null;
        var state = new InstallState(
            game.Dota.GameRoot, decision.TargetFolder, slot.Value, sha1, null, game.Dota.Build, DateTimeOffset.Now, effects, menuBackground, mouseColors, mouseNotTaken, mmr, autoAccept);
        try
        {
            if (decision.Action == PackAction.SwitchToCarrier)
            {
                previousVoice = game.Boot?.AudioLanguage ?? PackPlanner.English;
                BootConfigEditor.SetAudioLanguage(BootConfig.PathIn(game.Dota.GameRoot), decision.TargetFolder);
                state = state with { PreviousAudioLanguage = previousVoice };
            }

            store.Save(state);
        }
        catch (Exception ex)
        {
            // Without a saved state nobody would ever remove the pack: take it out right away.
            log.Error($"Install of {target} failed, the pack is taken out again", ex);
            File.Delete(target);
            throw;
        }
        log.Info($"Pack installed: {state.PakPath}, SHA1 {sha1}, voice language {(previousVoice is null ? "unchanged" : $"{previousVoice} -> {decision.TargetFolder}")}, blank effects {effects?.Values.Sum() ?? 0}, menu background {menuBackground ?? "game"}, MMR line {(mmr ? "on" : "off")}, auto accept {(autoAccept is { } delay ? $"{delay} s" : "off")}");
        return new InstallResult(state, null);
    }

    /// <summary>
    /// Puts new contents of files already in the installed pack in place, without building the
    /// pack again: every other file and the marker stay as they are. True when the pack holds
    /// these contents now, also when it held them before. False, with a log line, when there is
    /// no pack of ours in place, a file is not in it, or Dota runs and holds it open.
    /// </summary>
    /// <param name="describe">What the install state says about the pack after the change; the SHA-1 is set here.</param>
    public bool ReplaceFiles(GameState game, IReadOnlyDictionary<string, byte[]> files, Func<InstallState, InstallState>? describe = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(files);
        lock (_gate)
        {
            return ReplaceLocked(game, files, describe ?? (state => state));
        }
    }

    private bool ReplaceLocked(GameState game, IReadOnlyDictionary<string, byte[]> files, Func<InstallState, InstallState> describe)
    {
        InstallState? state = store.Load();
        if (state is null || game.DotaRunning || !File.Exists(state.PakPath)
            || !string.Equals(PackBuilder.Sha1OfFile(state.PakPath), state.Sha1, StringComparison.Ordinal))
        {
            log.Info($"Pack files not replaced: {(state is null ? "no pack" : game.DotaRunning ? "Dota is running" : "the pack is missing or changed")}");
            return false;
        }

        List<(string Path, byte[] Data)> entries = [];
        bool same = true;
        using (var package = new ValvePak.Package())
        {
            package.Read(state.PakPath);
            foreach (ValvePak.PackageEntry entry in package.Entries?.Values.SelectMany(e => e) ?? [])
            {
                string path = entry.GetFullPath();
                package.ReadEntry(entry, out byte[] data);
                if (files.TryGetValue(path, out byte[]? replacement))
                {
                    same &= replacement.AsSpan().SequenceEqual(data);
                    data = replacement;
                }

                entries.Add((path, data));
            }
        }

        string? absent = files.Keys.FirstOrDefault(path => !entries.Any(e => string.Equals(e.Path, path, StringComparison.Ordinal)));
        if (absent is not null)
        {
            log.Warning($"Pack files not replaced: {absent} is not in {state.PakPath}");
            return false;
        }

        if (same)
        {
            InstallState described = describe(state) with { Sha1 = state.Sha1 };
            if (described != state)
            {
                try
                {
                    store.Save(described);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Error($"Install state for {state.PakPath} not updated", ex);
                    return false;
                }
            }

            return true;
        }

        byte[] pak;
        using (var rebuilt = new ValvePak.Package())
        {
            foreach ((string path, byte[] data) in entries)
            {
                rebuilt.AddFile(path, data, multiChunk: false);
            }

            using var stream = new MemoryStream();
            rebuilt.Write(stream);
            pak = stream.ToArray();
        }

        string temp = state.PakPath + ".dotatune.tmp";
        File.WriteAllBytes(temp, pak);
        string sha1 = PackBuilder.Sha1(pak);
        bool saved = false;
        try
        {
            // The state names the new file first. A crash before the move leaves a state that does not
            // match the file: the pages ask to install the pack again, which leaves the old file as a
            // marked pack without a record, and Reset takes that out.
            store.Save(describe(state) with { Sha1 = sha1 });
            saved = true;
            File.Move(temp, state.PakPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (saved)
            {
                store.Save(state);
            }

            File.Delete(temp);
            log.Error($"Pack files not replaced in {state.PakPath}", ex);
            return false;
        }

        log.Info($"Pack files replaced in {state.PakPath}: {string.Join(", ", files.Keys)}, SHA1 {sha1}");
        return true;
    }

    /// <summary>
    /// Removes the pack if the file is still ours and returns the voice language if DotaTune
    /// changed it and the person did not change it since. Language folders, Valve files and
    /// other mods are never touched.
    /// </summary>
    public void Remove(GameState game)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_gate)
        {
            RemoveLocked(game);
        }
    }

    private void RemoveLocked(GameState game)
    {
        InstallState? state = store.Load();
        if (state is null)
        {
            return;
        }

        if (game.DotaRunning)
        {
            throw new InvalidOperationException("Dota is running, its files are locked");
        }

        if (File.Exists(state.PakPath))
        {
            if (string.Equals(PackBuilder.Sha1OfFile(state.PakPath), state.Sha1, StringComparison.Ordinal))
            {
                File.Delete(state.PakPath);
                log.Info($"Pack removed: {state.PakPath}");
            }
            else
            {
                log.Warning($"{state.PakPath} is not the file DotaTune wrote, left in place");
            }
        }

        string boot = BootConfig.PathIn(state.GameRoot);
        if (state.PreviousAudioLanguage is not null
            && File.Exists(boot)
            && string.Equals(BootConfig.Read(state.GameRoot)?.AudioLanguage, state.Language, StringComparison.Ordinal))
        {
            BootConfigEditor.SetAudioLanguage(boot, state.PreviousAudioLanguage);
            log.Info($"Voice language returned to {state.PreviousAudioLanguage}");
        }

        store.Clear();
    }

    public PackHealth Check(GameState game)
    {
        ArgumentNullException.ThrowIfNull(game);
        // Under the gate: a replace in between the state and the hash would read as a changed pack.
        lock (_gate)
        {
            return CheckLocked(game);
        }
    }

    private PackHealth CheckLocked(GameState game)
    {
        InstallState? state = store.Load();
        if (state is null)
        {
            return PackHealth.NotInstalled;
        }

        if (!File.Exists(state.PakPath))
        {
            return PackHealth.Missing;
        }

        if (!string.Equals(PackBuilder.Sha1OfFile(state.PakPath), state.Sha1, StringComparison.Ordinal))
        {
            return PackHealth.Changed;
        }

        if (!string.Equals(game.Decision?.TargetFolder, state.Language, StringComparison.Ordinal)
            && !string.Equals(game.Decision?.LoadedFolder, state.Language, StringComparison.Ordinal))
        {
            return PackHealth.FolderNotLoaded;
        }

        return string.Equals(game.Dota?.Build, state.GameBuild, StringComparison.Ordinal) ? PackHealth.Ready : PackHealth.OtherBuild;
    }

    private static bool IsInstallAction(PackAction action) =>
        action is PackAction.UseVoiceFolder or PackAction.SwitchToCarrier or PackAction.UseLaunchLanguageFolder;

    /// <summary>Slot 40, or the first of 41-63 not taken by another file. 01 is Valve, 64-67 belong to other managers.</summary>
    private int? FreeSlot(string folder)
    {
        for (int slot = DefaultSlot; slot <= LastSlot; slot++)
        {
            if (!File.Exists(Path.Combine(folder, InstallState.PakFileName(slot))))
            {
                return slot;
            }
        }

        return null;
    }

    private InstallResult Refuse(InstallRefusal reason)
    {
        log.Info($"Pack not installed: {reason}");
        return new InstallResult(null, reason);
    }
}
