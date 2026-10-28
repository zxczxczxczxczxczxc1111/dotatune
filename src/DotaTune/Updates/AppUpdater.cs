using DotaTune.Core.Diagnostics;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace DotaTune.Updates;

/// <summary>A newer version found by a check; the updater keeps in it what the download needs.</summary>
internal class AppUpdate(string version)
{
    public string Version { get; } = version;
}

/// <summary>What DotaTune asks of Velopack, so tests stand in for it without the network.</summary>
internal interface IAppUpdater
{
    /// <summary>False for a build started from its output folder: there is nothing to update.</summary>
    bool IsInstalled { get; }

    /// <summary>A newer version, or null when this one is the latest.</summary>
    Task<AppUpdate?> CheckAsync();

    /// <summary>Downloads the version; the progress comes in percent from another thread.</summary>
    Task DownloadAsync(AppUpdate update, Action<int> progress);

    /// <summary>
    /// Starts Velopack's updater, which waits for DotaTune to exit, puts the downloaded version in
    /// without a window of its own and, with <paramref name="restart"/>, starts DotaTune again.
    /// </summary>
    void InstallAfterExit(AppUpdate update, bool restart);
}

/// <summary>Releases of the public repository on GitHub, or a test source from <see cref="SourceVariable"/>.</summary>
internal sealed class VelopackUpdater : IAppUpdater
{
    public const string Repository = "https://github.com/zxczxczxczxczxczxc1111/dotatune";

    /// <summary>A folder or an address with releases instead of GitHub, for trial runs in a guest.</summary>
    public const string SourceVariable = "DOTATUNE_UPDATE_SOURCE";

    private readonly UpdateManager _manager;

    private VelopackUpdater(UpdateManager manager) => _manager = manager;

    public static VelopackUpdater Create(FileLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        string? source = Environment.GetEnvironmentVariable(SourceVariable);
        if (!string.IsNullOrWhiteSpace(source))
        {
            log.Info($"Updates come from {source} ({SourceVariable})");
            return new VelopackUpdater(new UpdateManager(source));
        }

        // No token: 60 requests an hour from one address are plenty for one check per start.
        return new VelopackUpdater(new UpdateManager(new GithubSource(Repository, null, false)));
    }

    public bool IsInstalled => _manager.IsInstalled;

    public async Task<AppUpdate?> CheckAsync()
    {
        UpdateInfo? info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return info is null ? null : new Found(info);
    }

    public Task DownloadAsync(AppUpdate update, Action<int> progress) =>
        _manager.DownloadUpdatesAsync(Info(update), progress, CancellationToken.None);

    public void InstallAfterExit(AppUpdate update, bool restart) =>
        _manager.WaitExitThenApplyUpdates(Info(update).TargetFullRelease, silent: true, restart: restart);

    private static UpdateInfo Info(AppUpdate update) =>
        update is Found found ? found.Info : throw new ArgumentException("The update was not found by this updater", nameof(update));

    private sealed class Found(UpdateInfo info) : AppUpdate(info.TargetFullRelease.Version.ToString())
    {
        public UpdateInfo Info { get; } = info;
    }
}

/// <summary>Velopack's own lines in DotaTune's log, from information up: the rest repeats on every check.</summary>
internal sealed class VelopackLog(FileLog log) : IVelopackLogger
{
    public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
    {
        string text = $"Velopack: {message}";
        switch (logLevel)
        {
            case VelopackLogLevel.Trace or VelopackLogLevel.Debug:
                return;
            case VelopackLogLevel.Information:
                log.Info(text);
                return;
            case VelopackLogLevel.Warning:
                log.Warning(exception is null ? text : $"{text}: {exception.Message}");
                return;
            default:
                log.Error(text, exception);
                return;
        }
    }
}
