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
            desktop.MainWindow = CreateMainWindow(SectionKind.GameState);
            // The readings of a session played without DotaTune are in console.log until the next launch.
            _ = Task.Run(() => Services.RefreshMmr());
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Builds the main window with texts of the current UI culture. A language switch
    /// builds a new window instead of patching every label of the old one.
    /// </summary>
    internal static MainWindow CreateMainWindow(SectionKind section)
    {
        var settings = new SettingsViewModel(Services);
        var window = new MainWindow { DataContext = new MainViewModel(Services, settings, section) };
        settings.LanguageChanged += code => SwitchLanguage(window, code);
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
