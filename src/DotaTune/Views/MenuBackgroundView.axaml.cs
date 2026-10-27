using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DotaTune.Resources;
using DotaTune.ViewModels;

namespace DotaTune.Views;

public partial class MenuBackgroundView : UserControl
{
    private static readonly FilePickerFileType PictureType = new(Strings.MenuPictureFileType)
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"],
    };

    public MenuBackgroundView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MenuBackgroundViewModel page)
        {
            return;
        }

        // Gives the page the file dialog of the window it is shown in.
        page.PickPicturePath = async () =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top)
            {
                return null;
            }

            IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = [PictureType],
            });
            return files.Count == 0 ? null : files[0].TryGetLocalPath();
        };
    }
}
