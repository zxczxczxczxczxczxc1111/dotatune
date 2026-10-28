using System.Runtime.InteropServices;

namespace DotaTune.Core.Platform;

/// <summary>Brings an open DotaTune window forward: from a second start of the program and from the installer.</summary>
public static partial class WindowFront
{
    private const int RestoreWindow = 9;

    /// <summary>False when Windows kept the foreground for another window.</summary>
    public static bool Bring(IntPtr window)
    {
        // Only a minimised window is restored: restoring a maximised one would shrink it.
        if (IsIconic(window))
        {
            ShowWindow(window, RestoreWindow);
        }

        return SetForegroundWindow(window);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr window);
}
