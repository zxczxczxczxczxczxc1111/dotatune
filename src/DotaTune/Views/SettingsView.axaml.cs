using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DotaTune.Core.Presets;
using DotaTune.Resources;
using DotaTune.ViewModels;

namespace DotaTune.Views;

public partial class SettingsView : UserControl
{
    private static readonly FilePickerFileType PresetType = new(Strings.PresetFileType) { Patterns = ["*" + PresetFile.Extension] };
    private static readonly FilePickerFileType BackupType = new(Strings.BackupFileType) { Patterns = ["*" + SettingsBackupFile.Extension] };

    public SettingsView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ConnectDialogs();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ConnectDialogs();
    }

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

    /// <summary>Gives the copy and presets blocks the dialogs and the clipboard of the window they are shown in.</summary>
    private void ConnectDialogs()
    {
        if (DataContext is not SettingsViewModel { Config: { Presets: var presets, Backup: var backup } })
        {
            return;
        }

        // The copy is offered for the desktop: it is easy to find there after a reset or to carry away.
        backup.PickSavePath = async name =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top)
            {
                return null;
            }

            IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = name,
                DefaultExtension = SettingsBackupFile.Extension,
                FileTypeChoices = [BackupType],
                SuggestedStartLocation = await top.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Desktop),
            });
            return file?.TryGetLocalPath();
        };
        backup.PickOpenPath = async () =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top)
            {
                return null;
            }

            IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = [BackupType],
                SuggestedStartLocation = await top.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Desktop),
            });
            return files.Count == 0 ? null : files[0].TryGetLocalPath();
        };

        presets.PickSavePath = async () =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top)
            {
                return null;
            }

            IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = "DotaTune" + PresetFile.Extension,
                DefaultExtension = PresetFile.Extension,
                FileTypeChoices = [PresetType],
            });
            return file?.TryGetLocalPath();
        };
        presets.PickOpenPath = async () =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top)
            {
                return null;
            }

            IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = [PresetType],
            });
            return files.Count == 0 ? null : files[0].TryGetLocalPath();
        };
        presets.CopyText = async text =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                // The clipboard owns the data from here and disposes of it itself.
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateText(text));
                await clipboard.SetDataAsync(data);
            }
        };
    }
}
