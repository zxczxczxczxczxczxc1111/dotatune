using System.Runtime.InteropServices;
using System.Text;

namespace DotaTune.Core.Text;

/// <summary>
/// Code pages of this Windows installation. They come from the system, not from
/// <see cref="System.Globalization.CultureInfo"/>: an English Windows with Russian
/// regional format still has ANSI 1252, and an English Windows with the Russian
/// "language for non-Unicode programs" has ANSI 1251 with an English UI.
/// </summary>
public static partial class CodePages
{
    /// <summary>Makes 866, 1251 and the other legacy code pages available to <see cref="Encoding.GetEncoding(int)"/>.</summary>
    public static void Register() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>ANSI code page used by non-Unicode programs, 1251 on a Russian machine.</summary>
    public static int Ansi => checked((int)GetACP());

    /// <summary>OEM code page of console programs, 866 on a Russian machine.</summary>
    public static int Oem => checked((int)GetOEMCP());

    [LibraryImport("kernel32.dll")]
    private static partial uint GetACP();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetOEMCP();
}
