namespace DotaTune.Core.Platform;

/// <summary>
/// Where DotaTune keeps its own files. None of them live in the install folder:
/// Velopack deletes %LocalAppData%\DotaTune entirely on uninstall, and the
/// uninstall log and pack state must outlive it.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string dataRoot, string workRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);
        DataRoot = Path.GetFullPath(dataRoot);
        WorkRoot = Path.GetFullPath(workRoot);
    }

    /// <summary>Settings, logs and pack state: %AppData%\DotaTune. May contain a Cyrillic user name.</summary>
    public string DataRoot { get; }

    /// <summary>
    /// Scratch space for external programs: C:\ProgramData\DotaTune. %TEMP% is not used
    /// because it contains the user name, which tools working in ANSI cannot open.
    /// </summary>
    public string WorkRoot { get; }

    public string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public string LogDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>What DotaTune put into the game, so it can be checked and taken out again.</summary>
    public string InstallStateFile => Path.Combine(DataRoot, "install.json");

    /// <summary>What DotaTune wrote into autoexec.cfg and the saved values it replaced.</summary>
    public string AutoexecStateFile => Path.Combine(DataRoot, "autoexec.json");

    /// <summary>Launch lines DotaTune replaced, one per account, for "put back" and uninstall.</summary>
    public string LaunchStateFile => Path.Combine(DataRoot, "launch.json");

    /// <summary>What transfers between accounts replaced; the copies are in <see cref="TransferBackupRoot"/>.</summary>
    public string TransferStateFile => Path.Combine(DataRoot, "transfer.json");

    public string TransferBackupRoot => Path.Combine(DataRoot, "transfer");

    /// <summary>Accounts whose hero grids have DotaTune layouts; their copies are in <see cref="HeroGridFolder"/>.</summary>
    public string HeroGridStateFile => Path.Combine(DataRoot, "herogrids.json");

    /// <summary>Copies of hero grid files before the first change and the downloaded layouts.</summary>
    public string HeroGridFolder => Path.Combine(DataRoot, "herogrids");

    /// <summary>Pictures added for the main menu background, already cut to the shape the game shows.</summary>
    public string MenuPictureFolder => Path.Combine(DataRoot, "menu-pictures");

    /// <summary>Every MMR reading taken from the game, oldest first.</summary>
    public string MmrHistoryFile => Path.Combine(DataRoot, "mmr.json");

    public static AppPaths ForCurrentUser() => new(
        Path.Combine(KnownFolder(Environment.SpecialFolder.ApplicationData), "DotaTune"),
        Path.Combine(KnownFolder(Environment.SpecialFolder.CommonApplicationData), "DotaTune"));

    private static string KnownFolder(Environment.SpecialFolder folder)
    {
        string path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        if (path.Length == 0)
        {
            throw new InvalidOperationException($"Windows did not return the {folder} folder.");
        }

        return path;
    }
}
