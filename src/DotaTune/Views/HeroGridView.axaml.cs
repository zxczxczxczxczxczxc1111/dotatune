using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DotaTune.Resources;
using DotaTune.ViewModels;

namespace DotaTune.Views;

public partial class HeroGridView : UserControl
{
    private static readonly FilePickerFileType GridType = new(Strings.GridFileType) { Patterns = ["*.json"] };

    public HeroGridView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Connect();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Connect();
    }

    /// <summary>Gives the page the file dialog of the window it is shown in.</summary>
    private void Connect()
    {
        if (DataContext is not HeroGridViewModel page)
        {
            return;
        }

        page.PickOpenPath = async () =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top)
            {
                return null;
            }

            IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = [GridType, FilePickerFileTypes.All],
            });
            return files.Count == 0 ? null : files[0].TryGetLocalPath();
        };
    }
}
