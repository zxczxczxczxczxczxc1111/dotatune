using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DotaTune.Core.Diagnostics;

namespace DotaTune.Core.Platform;

/// <summary>
/// One DotaTune per program folder and Windows session. The copy that runs holds a named mutex; a
/// second start from the same folder finds the name taken, brings the open window forward and
/// leaves. A build started from another folder, as one from the sources, runs beside it. The mutex
/// is never owned, only held open: Windows removes it with the last handle, also when the copy is stopped.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;

    private SingleInstance(Mutex? mutex, bool isFirst)
    {
        _mutex = mutex;
        IsFirst = isFirst;
    }

    /// <summary>False when another copy from the same folder runs in this session.</summary>
    public bool IsFirst { get; }

    /// <summary>
    /// The name for the copies of one folder; per session (Local\), so another user signed in on
    /// the same PC runs a copy of their own. Velopack updates the files in place, the folder stays.
    /// </summary>
    public static string NameFor(string programFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programFolder);
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFolder)).ToUpperInvariant();
        return @"Local\DotaTune.Running." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..16];
    }

    /// <summary>A name Windows refuses to create lets this copy run, without the guard.</summary>
    public static SingleInstance Claim(string name, FileLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            var mutex = new Mutex(initiallyOwned: false, name, out bool createdNew);
            if (createdNew)
            {
                return new SingleInstance(mutex, isFirst: true);
            }

            mutex.Dispose();
            return new SingleInstance(null, isFirst: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            log.Warning($"No guard against a second copy: {ex.Message}");
            return new SingleInstance(null, isFirst: true);
        }
    }

    /// <summary>
    /// The window of the copy started from <paramref name="exePath"/>; false when it has none or
    /// Windows kept the foreground.
    /// </summary>
    public static bool BringOtherForward(string exePath, FileLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(log);
        int self = Environment.ProcessId;
        Process[] running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath));
        try
        {
            foreach (Process process in running)
            {
                IntPtr window = process.Id == self ? IntPtr.Zero : WindowOf(process, exePath);
                if (window != IntPtr.Zero)
                {
                    bool front = WindowFront.Bring(window);
                    log.Info($"DotaTune {process.Id} is already open, brought forward: {front}");
                    return front;
                }
            }

            // Still starting, or closing: its window comes, or after an update with a restart it
            // starts again itself; a start while it closes for an install on exit is lost.
            log.Info("DotaTune is already running without a window");
            return false;
        }
        finally
        {
            foreach (Process process in running)
            {
                process.Dispose();
            }
        }
    }

    public void Dispose() => _mutex?.Dispose();

    /// <summary>The main window of a copy of the same file; zero for another file, another session or an exited copy.</summary>
    private static IntPtr WindowOf(Process process, string exePath)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase)
                ? process.MainWindowHandle
                : IntPtr.Zero;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // It has just exited, or it belongs to another session or user.
            return IntPtr.Zero;
        }
    }
}
