using Avalonia.Controls;

namespace DotaTune.Setup;

public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
    }

    internal static SetupWindow Open(SetupViewModel model)
    {
        var window = new SetupWindow { DataContext = model };
        model.Finished += window.Close;
        return window;
    }

    // Velopack cannot be stopped half way, so the window stays until the installation is over.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is SetupViewModel { IsBusy: true })
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
