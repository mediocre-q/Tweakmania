using System.Windows;
using System.Windows.Controls;

namespace Tweak.App;

public partial class SetupWindow : Window
{
    public SetupWindow(MainViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        viewModel.RefreshProxyStatus();
        Activated += (_, _) => viewModel.RefreshProxyStatus();
        if (Application.Current?.MainWindow?.TryFindResource(typeof(Button)) is Style buttonStyle)
            Resources[typeof(Button)] = buttonStyle;
    }

}
