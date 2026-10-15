using System.Windows;
using System.Windows.Controls;
using Tweak.Core;

namespace Tweak.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        StateChanged += (_, _) => UpdatePreviewVisibility(viewModel);
        IsVisibleChanged += (_, _) => UpdatePreviewVisibility(viewModel);
        UpdatePreviewVisibility(viewModel);
    }

    private void UpdatePreviewVisibility(MainViewModel viewModel)
    {
        viewModel.PreviewVisible = IsVisible && WindowState != WindowState.Minimized;
    }

    private void CurveEditor_CurveEdited(CurvePoint[] points)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.UpdateCurveFromGraph(points);
    }

    public void CommitCurveGridEdit()
    {
        CurveGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        CurveGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }

    private void Setup_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            var window = new SetupWindow(viewModel) { Owner = this };
            window.ShowDialog();
        }
    }

}
