using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DotaTune.Core.Platform;
using DotaTune.ViewModels;
using DotaTune.Views;

namespace DotaTune;

public partial class App : Application
{
    private static AppServices? _services;

    /// <summary>Set by <see cref="Program"/>; tests and the designer get a default instance.</summary>
    internal static AppServices Services
    {
        get => _services ??= AppServices.Create(AppPaths.ForCurrentUser());
        set => _services = value;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            UiLanguage.Apply(UiLanguage.Resolve(Services.Settings.Load().Language));
            // Before the pages read the settings: it writes the effect groups out.
            Services.News.Detect();
            desktop.MainWindow = CreateMainWindow(SectionKind.GameState);
            // The readings of a session played without DotaTune are in console.log until the next launch.
            Task mmr = Task.Run(() => Services.RefreshMmr());
            // The start refreshes the MMR line in the pack, so an update waits for it as for a page.
            Services.Updates.IsWorking = () =>
                !mmr.IsCompleted || Services.News.IsRebuilding || (desktop.MainWindow?.DataContext as MainViewModel)?.IsWorking == true;
            Services.Updates.KeepsPanelClosed = () => Services.News.IsOpen;
            Services.Updates.Shutdown = () => desktop.Shutdown();
            _ = Services.Updates.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Builds the main window with texts of the current UI culture. A language switch
    /// builds a new window instead of patching every label of the old one.
    /// </summary>
    internal static MainWindow CreateMainWindow(SectionKind section)
    {
        var main = new MainViewModel(Services, section);
        var window = new MainWindow { DataContext = main };
        main.Settings.LanguageChanged += code => SwitchLanguage(window, code);
        return window;
    }

    private static void SwitchLanguage(MainWindow old, string? code)
    {
        UiLanguage.Apply(UiLanguage.Resolve(code));
        MainWindow fresh = CreateMainWindow(SectionKind.Settings);
        fresh.Position = old.Position;
        fresh.Width = old.Width;
        fresh.Height = old.Height;
        fresh.WindowState = old.WindowState;

        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = fresh;
        }

        fresh.Show();
        old.Close();
        Services.Log.Info($"Interface language switched to {System.Globalization.CultureInfo.CurrentUICulture.Name}");
    }
}
