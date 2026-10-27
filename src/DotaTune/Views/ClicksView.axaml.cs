using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using DotaTune.ViewModels;

namespace DotaTune.Views;

public partial class ClicksView : UserControl
{
    public ClicksView() => InitializeComponent();

    /// <summary>The picked colour reaches the row once, when the picker closes.</summary>
    private void OnPickerClosed(object? sender, EventArgs e)
    {
        if (sender is PopupFlyoutBase { Target.DataContext: ColorRow row })
        {
            row.TakePicked();
        }
    }
}
