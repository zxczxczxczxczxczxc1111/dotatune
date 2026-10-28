using System.ComponentModel;
using System.Diagnostics;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Platform;

namespace DotaTune.Setup;

/// <summary>What the installer does to the machine; tests swap it for a stand-in.</summary>
internal interface ISetupSteps
{
    /// <summary>The installer was built with Velopack's setup.exe inside.</summary>
    bool HasPayload { get; }

    /// <summary>DotaTune from the install folder is running.</summary>
    bool AppRunning();

    /// <summary>Asks every running DotaTune to close and waits; false when one is still there.</summary>
    Task<bool> CloseAppAsync();

    /// <summary>
    /// Runs Velopack's setup.exe without its windows; its exit code, 0 when installed, or
    /// <see cref="SetupSteps.StillRunning"/> when it has not finished in a few minutes.
    /// </summary>
    Task<int> InstallAsync();

    /// <summary>Brings the running DotaTune to the front or starts it; false when neither worked.</summary>
    bool OpenApp();

    void OpenLogs();
}

internal sealed class SetupSteps : ISetupSteps
{
    /// <summary>What <see cref="InstallAsync"/> returns when Velopack has not finished in time.</summary>
    public const int StillRunning = -2;

    private const string PayloadName = "Payload.Setup.exe";
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InstallWait = TimeSpan.FromMinutes(5);

    private readonly string _root;
    private readonly string _logDirectory;
    private readonly FileLog _log;

    public SetupSteps(string root, string logDirectory, FileLog log)
    {
        _root = root;
        _logDirectory = logDirectory;
        _log = log;
    }

    public bool HasPayload => typeof(SetupSteps).Assembly.GetManifestResourceInfo(PayloadName) is not null;

    public bool AppRunning()
    {
        Process[] running = Running();
        foreach (Process process in running)
        {
            process.Dispose();
        }

        return running.Length > 0;
    }

    public async Task<bool> CloseAppAsync()
    {
        Process[] running = Running();
        try
        {
            var asked = new List<Process>();
            foreach (Process process in running)
            {
                // A close request, as the window's own close button: the program finishes what it writes.
                bool sent = process.CloseMainWindow();
                _log.Info($"Asking DotaTune {process.Id} to close: {sent}");
                if (sent)
                {
                    asked.Add(process);
                }
            }

            if (asked.Count < running.Length)
            {
                // A copy without a window cannot be asked; Velopack stops it, waiting would change nothing.
                _log.Warning("DotaTune has no window to close, Velopack will stop it");
                return false;
            }

            using var timeout = new CancellationTokenSource(CloseWait);
            try
            {
                await Task.WhenAll(asked.Select(p => p.WaitForExitAsync(timeout.Token)));
                return true;
            }
            catch (OperationCanceledException)
            {
                // Velopack stops what is left before it replaces the files.
                _log.Warning($"DotaTune did not close in {CloseWait.TotalSeconds} s, Velopack will stop it");
                return false;
            }
        }
        finally
        {
            foreach (Process process in running)
            {
                process.Dispose();
            }
        }
    }

    public async Task<int> InstallAsync()
    {
        // %TEMP% holds the user name and may have Cyrillic in it. That is fine here: Velopack works in
        // Unicode, only the Valve compiler needs ASCII paths.
        string folder = Path.Combine(Path.GetTempPath(), "DotaTune-setup-" + Guid.NewGuid().ToString("N")[..8]);
        string setup = Path.Combine(folder, "Setup.exe");
        string velopackLog = Path.Combine(_logDirectory, "setup-velopack.log");
        try
        {
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(_logDirectory);
            await using (Stream payload = typeof(SetupSteps).Assembly.GetManifestResourceStream(PayloadName)
                ?? throw new InvalidOperationException("The installer has no Velopack setup inside."))
            await using (FileStream file = File.Create(setup))
            {
                await payload.CopyToAsync(file);
            }

            var start = new ProcessStartInfo(setup)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = folder,
                ArgumentList = { "--silent", "--log", velopackLog },
            };
            _log.Info($"Running Velopack setup, log {velopackLog}");
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Velopack setup did not start.");
            using var timeout = new CancellationTokenSource(InstallWait);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Not stopped: a setup killed half way leaves neither the old version nor the new one.
                // The window gets its buttons back, and Velopack finishes or fails on its own.
                _log.Error($"Velopack setup {process.Id} still runs after {InstallWait.TotalMinutes} min, the installer stops waiting");
                return StillRunning;
            }

            _log.Info($"Velopack setup exited with code {process.ExitCode}");
            return process.ExitCode;
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning($"Temporary folder {folder} not removed: {ex.Message}");
            }
        }
    }

    public bool OpenApp()
    {
        Process[] running = Running();
        try
        {
            // A running copy is brought forward from here; started again, it would do the same itself.
            if (running.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) is { } open)
            {
                bool front = WindowFront.Bring(open.MainWindowHandle);
                _log.Info($"DotaTune {open.Id} is already open, brought forward: {front}");
                return true;
            }

            if (running.Length > 0)
            {
                _log.Info("DotaTune is already running without a window");
                return true;
            }
        }
        finally
        {
            foreach (Process process in running)
            {
                process.Dispose();
            }
        }

        string exe = Path.Combine(_root, "DotaTune.exe");
        try
        {
            // The working folder is the install folder, so the started program does not hold the temporary one.
            using Process? started = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = _root });
            _log.Info($"Started {exe}");
            return started is not null;
        }
        catch (Win32Exception ex)
        {
            _log.Error($"{exe} did not start", ex);
            return false;
        }
    }

    public void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            using Process? explorer = Process.Start(new ProcessStartInfo(_logDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            _log.Error($"Log folder {_logDirectory} did not open", ex);
        }
    }

    /// <summary>DotaTune processes started from the install folder, as Velopack finds the ones it stops.</summary>
    private Process[] Running()
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root)) + Path.DirectorySeparatorChar;
        var found = new List<Process>();
        foreach (Process process in Process.GetProcessesByName("DotaTune"))
        {
            string? path;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Another user's process or one that has just exited: not ours to close.
                _log.Info($"DotaTune {process.Id} skipped: {ex.Message}");
                path = null;
            }

            if (path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return [.. found];
    }
}
