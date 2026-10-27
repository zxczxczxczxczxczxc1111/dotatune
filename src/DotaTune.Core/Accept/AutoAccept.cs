using System.Globalization;
using System.Text;
using DotaTune.Core.Effects;
using DotaTune.Core.Game;

namespace DotaTune.Core.Accept;

/// <summary>
/// Accepting a found match on its own. The game's own script of the "match found" window gets a
/// few lines after Valve's text: after the chosen delay they do what the accept button does,
/// unless the player accepted first or the window is gone. A decline leaves no mark the lines
/// can see. The window's layout is not touched, and Valve's text is taken from the current game
/// at every build, so it keeps up with updates. Each new window runs the script anew: seen on
/// build 6968 with the window opened twice by UIShowCustomLayoutPopup from the menu.
/// </summary>
public static class AutoAccept
{
    public const string ScriptPath = "panorama/scripts/popups/popup_accept_match.vjs_c";
    public const int MinDelay = 1;
    public const int MaxDelay = 15;
    public const int DefaultDelay = 5;

    private const string Mark = "// DotaTune auto accept";

    // The button: DOTAPlayAcceptMatch() AddStyle( PopupAcceptDeclineMatchPanel, Accepted ) AddStyle( PopupReadyUpPlayersPanel, Accepted ).
    private const string Lines = Mark + """
        : presses Accept after {DELAY} s unless the player accepted first.
        (function () {
        	var popup = $.GetContextPanel();
        	$.Schedule({DELAY}, function () {
        		if (!popup.IsValid()) {
        			return;
        		}

        		var accept = popup.FindChildTraverse('PopupAcceptDeclineMatchPanel');
        		if (!accept || accept.BHasClass('Accepted')) {
        			return;
        		}

        		$.DispatchEvent('DOTAPlayAcceptMatch');
        		accept.AddClass('Accepted');
        		var ready = popup.FindChildTraverse('PopupReadyUpPlayersPanel');
        		if (ready) {
        			ready.AddClass('Accepted');
        		}

        		$.Msg('DOTATUNE_ACCEPT after {DELAY} s');
        	});
        })();

        """;

    /// <summary>The delay kept in the settings, put into the range the page offers; null gives the default.</summary>
    public static int Delay(int? chosen) => Math.Clamp(chosen ?? DefaultDelay, MinDelay, MaxDelay);

    /// <summary>Reads the script of the window from game\dota\pak01_dir.vpk. Only reads.</summary>
    /// <exception cref="FileNotFoundException">The game has no pak01_dir.vpk.</exception>
    /// <exception cref="InvalidDataException">The archive cannot be read or has no such script.</exception>
    public static byte[] ReadGameScript(string dotaFolder) => GameArchive.ReadFile(dotaFolder, ScriptPath);

    /// <summary>Null when the game's script can take the lines; otherwise why not, for the log.</summary>
    public static string? Problem(ReadOnlySpan<byte> gameScript)
    {
        try
        {
            return Text(gameScript).Contains(Mark, StringComparison.Ordinal) ? "the game's script already has DotaTune lines" : null;
        }
        catch (InvalidDataException ex)
        {
            return $"the script of the accept window is not readable: {ex.Message}";
        }
    }

    /// <summary>The game's script with the lines added, for the pack.</summary>
    /// <exception cref="InvalidDataException">The script is not a compiled script with its text last.</exception>
    public static byte[] PackFile(ReadOnlySpan<byte> gameScript, int delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, MinDelay);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delay, MaxDelay);
        // The raw string takes the line ends of this source file; Valve's text has CRLF.
        string lines = Lines.Replace("{DELAY}", delay.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal).ReplaceLineEndings("\r\n");
        return CompiledResource.WithData(gameScript, Encoding.UTF8.GetBytes($"{Text(gameScript)}\r\n\r\n{lines}"));
    }

    /// <summary>The text of a compiled script: the DATA block, UTF-8 without zero bytes.</summary>
    private static string Text(ReadOnlySpan<byte> script)
    {
        ReadOnlySpan<byte> data = CompiledResource.Data(script);
        if (data.Contains((byte)0))
        {
            throw new InvalidDataException("the DATA block is not script text");
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("the script text is not UTF-8", ex);
        }
    }
}
