using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tweak.App;

internal static class NameDialog
{
    public static string? Show(string title, string prompt, string initial, Window? owner, string actionLabel)
    {
        var window = new Window { Title = title, Width = 390, SizeToContent = SizeToContent.Height, MinWidth = 390, MaxWidth = 390, MinHeight = 185,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner, ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(16, 24, 32)), Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };
        if ((owner ?? Application.Current?.MainWindow)?.TryFindResource(typeof(Button)) is Style buttonStyle)
            window.Resources[typeof(Button)] = buttonStyle;
        var border = new Border { Background = new SolidColorBrush(Color.FromRgb(24, 35, 45)), BorderBrush = new SolidColorBrush(Color.FromRgb(48, 64, 77)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Margin = new Thickness(14) };
        var panel = new DockPanel(); border.Child = panel;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(13, 6, 13, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var ok = new Button { Content = actionLabel, Padding = new Thickness(15, 6, 15, 6), Background = new SolidColorBrush(Color.FromRgb(30, 143, 146)), Foreground = new SolidColorBrush(Color.FromRgb(7, 25, 29)), IsDefault = true };
        buttons.Children.Add(cancel); buttons.Children.Add(ok); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var content = new StackPanel(); DockPanel.SetDock(content, Dock.Top); panel.Children.Add(content);
        content.Children.Add(new TextBlock { Text = prompt, Foreground = new SolidColorBrush(Color.FromRgb(145, 162, 175)), Margin = new Thickness(0, 0, 0, 6) });
        var box = new TextBox { Text = initial, Padding = new Thickness(8, 6, 8, 6), Background = new SolidColorBrush(Color.FromRgb(17, 27, 35)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(52, 70, 82)), CaretBrush = new SolidColorBrush(Color.FromRgb(57, 213, 208)) };
        content.Children.Add(box);
        ok.Click += (_, _) => { if (string.IsNullOrWhiteSpace(box.Text)) { box.Focus(); return; } window.DialogResult = true; };
        window.Content = border; window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
