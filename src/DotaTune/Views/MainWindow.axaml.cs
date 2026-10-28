using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace DotaTune.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>"Open the log" of a failed update: today's file, where the reason is.</summary>
    private async void OnOpenLog(object? sender, RoutedEventArgs e)
    {
        string file = App.Services.Log.CurrentFile;
        try
        {
            bool opened = File.Exists(file) && await Launcher.LaunchFileInfoAsync(new FileInfo(file));
            if (!opened)
            {
                App.Services.Log.Warning($"The log {file} did not open");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Services.Log.Error($"Could not open the log {file}", ex);
        }
    }
}
