using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DotaTune.ViewModels;

namespace DotaTune.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private async void OnOpenDataFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(settings.DataFolder);
            bool opened = await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(settings.DataFolder));
            if (!opened)
            {
                App.Services.Log.Warning($"Explorer did not open {settings.DataFolder}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Services.Log.Error($"Could not open {settings.DataFolder}", ex);
        }
    }
}
