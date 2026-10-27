using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using DotaTune.Core.Presets;
using DotaTune.Resources;
using DotaTune.ViewModels;

namespace DotaTune.Views;

public partial class ConfigView : UserControl
{
    private static readonly FilePickerFileType PresetType = new(Strings.PresetFileType) { Patterns = ["*" + PresetFile.Extension] };

    public ConfigView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ConnectPresets();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ConnectPresets();
    }

    /// <summary>Gives the presets block the dialogs and the clipboard of the window it is shown in.</summary>
    private void ConnectPresets()
    {
        if (DataContext is not ConfigViewModel { Presets: var presets })
        {
            return;
        }

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
