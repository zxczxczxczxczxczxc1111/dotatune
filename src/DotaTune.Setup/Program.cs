using Avalonia;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Platform;
using DotaTune.Core.Settings;

namespace DotaTune.Setup;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The Windows display language has to be read before anything changes the UI culture.
        UiLanguage.CaptureWindowsLanguage();
        AppPaths paths = AppPaths.ForCurrentUser();
        var log = new FileLog(paths.LogDirectory);
        // An update speaks the language chosen in the program; a first install has no choice yet.
        UiLanguage.Apply(UiLanguage.Resolve(new SettingsStore(paths.SettingsFile, log).Load().Language));

        string root = Installation.DefaultRoot();
        Version version = Installation.ThisVersion();
        InstalledApp installed = Installation.Find(root, log);
        App.Model = new SetupViewModel(Installation.Decide(installed, version), version, installed.Version, new SetupSteps(root, paths.LogDirectory, log), log);

        try
        {
            return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            log.Error("Unhandled exception, the installer stops", ex);
            throw;
        }
    }
}
