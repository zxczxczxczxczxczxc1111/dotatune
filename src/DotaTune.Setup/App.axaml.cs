using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DotaTune.Setup;

public partial class App : Application
{
    /// <summary>Set by <see cref="Program"/> before the window is built.</summary>
    internal static SetupViewModel? Model { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = SetupWindow.Open(Model ?? throw new InvalidOperationException("The installer started without its model."));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
