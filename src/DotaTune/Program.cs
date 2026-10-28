using Avalonia;
using DotaTune.Core.HeroGrids;
using DotaTune.Core.Platform;
using DotaTune.Core.Text;
using DotaTune.Updates;
using Velopack;

namespace DotaTune;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The Windows display language has to be read before anything changes the UI culture.
        UiLanguage.CaptureWindowsLanguage();
        CodePages.Register();

        AppServices services = AppServices.Create(
            AppPaths.ForCurrentUser(),
            gridDownload: cancel => GridDownload.FromInternetAsync(AppServices.AppVersion, cancel),
            updater: VelopackUpdater.Create);

        // A second start from this folder only brings the open window forward, and before Velopack:
        // it would put a version downloaded for the exit in at once and stop the open copy, maybe in
        // the middle of writing the game files. Velopack's hooks run whatever is open.
        using SingleInstance? guard = IsVelopackHook(args)
            ? null
            : SingleInstance.Claim(SingleInstance.NameFor(AppContext.BaseDirectory), services.Log);
        if (guard is { IsFirst: false })
        {
            if (Environment.ProcessPath is { } exe)
            {
                SingleInstance.BringOtherForward(exe, services.Log);
            }

            return 0;
        }

        // Velopack runs install and uninstall hooks from here and exits right after them,
        // so it goes before any window is created. A version downloaded but not installed
        // before goes in here too, before anything writes the game files.
        VelopackApp.Build()
            .SetLogger(new VelopackLog(services.Log))
            .OnAfterInstallFastCallback(version => services.Log.Info($"Installed version {version}"))
            .OnBeforeUninstallFastCallback(version =>
            {
                services.Log.Info($"Uninstalling version {version}");
                services.RemoveFromGame();
            })
            .Run();

        services.Log.Info($"Started, ANSI {CodePages.Ansi}, OEM {CodePages.Oem}, UI {UiLanguage.Windows.Name}, format {System.Globalization.CultureInfo.CurrentCulture.Name}");
        App.Services = services;

        try
        {
            int code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            services.Updates.InstallOnExit();
            return code;
        }
        catch (Exception ex)
        {
            services.Log.Error("Unhandled exception, the program stops", ex);
            throw;
        }
    }

    /// <summary>Velopack starts the program with --veloapp-install, -updated, -obsolete or -uninstall for its hooks.</summary>
    internal static bool IsVelopackHook(string[] args) =>
        args.Any(a => a.StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
