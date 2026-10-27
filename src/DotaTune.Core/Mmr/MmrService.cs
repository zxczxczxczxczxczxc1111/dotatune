using DotaTune.Core.Diagnostics;
using DotaTune.Core.Game;
using DotaTune.Core.Pack;

namespace DotaTune.Core.Mmr;

/// <summary>
/// The MMR line between game sessions: readings the menu script printed into console.log go
/// to mmr.json, and the data file of the installed pack gets the newest of them, so the next
/// session counts the change from there.
/// </summary>
public sealed class MmrService(MmrHistoryStore history, PackService packs, FileLog log)
{
    // console.log as it was when all its readings were taken: unchanged, it is not read again.
    private LogMark _taken = LogMark.None;

    public MmrHistoryStore History => history;

    /// <summary>Takes the readings from console.log into the history; returns how many were new.</summary>
    /// <exception cref="InvalidDataException">mmr.json is there but cannot be read.</exception>
    public int Collect(GameState game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Dota is null)
        {
            return 0;
        }

        string file = ConsoleLog.PathIn(game.Dota.GameRoot);
        string text;
        LogMark mark;
        try
        {
            mark = ConsoleLog.MarkOf(file);
            if (mark.Length == 0 || (mark.Length == _taken.Length && mark.Head.AsSpan().SequenceEqual(_taken.Head)))
            {
                return 0;
            }

            text = ConsoleLog.ReadAfter(file, LogMark.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"{file} is not readable, its MMR readings are not taken: {ex.Message}");
            return 0;
        }

        int added = history.Add(MmrLog.Parse(text));
        // The game may have written more after the mark; that part is read again next time.
        _taken = mark;
        if (added > 0)
        {
            log.Info($"MMR readings taken from {file}: {added}");
        }

        return added;
    }

    /// <summary>
    /// Puts the newest reading of every account into the installed pack when it carries the MMR
    /// line. False when there is no such pack, Dota runs or the pack could not be changed.
    /// </summary>
    /// <param name="language">"ru" or "en": the words of the line in the menu.</param>
    /// <exception cref="InvalidDataException">mmr.json or install.json is there but cannot be read.</exception>
    public bool RefreshPack(GameState game, string language)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (packs.Current is not { Mmr: true })
        {
            return false;
        }

        byte[] data = MmrMenu.DataFile(MmrHistoryStore.Latest(history.Load()), language);
        return packs.ReplaceFiles(game, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [MmrMenu.DataPath] = data });
    }
}
