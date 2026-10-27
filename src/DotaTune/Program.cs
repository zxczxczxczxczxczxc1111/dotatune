using Avalonia;
using DotaTune.Core.HeroGrids;
using DotaTune.Core.Platform;
using DotaTune.Core.Text;
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
            gridDownload: cancel => GridDownload.FromInternetAsync(AppServices.AppVersion, cancel));

        // Velopack runs install and uninstall hooks from here and exits right after them,
        // so it goes before any window is created.
        VelopackApp.Build()
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
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            services.Log.Error("Unhandled exception, the program stops", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
