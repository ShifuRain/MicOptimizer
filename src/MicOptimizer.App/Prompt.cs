using System.Windows;
using System.Windows.Controls;

namespace MicOptimizer.App;

/// <summary>Two small dialogs the window needs: ask for a name, and confirm a destructive action.</summary>
public static class Prompt
{
    public static string? AskText(Window owner, string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 300, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 6, 0, 16) };
        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 84, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 84 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title, Owner = owner, Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("PanelBrush"),
            FontFamily = owner.FontFamily,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }

    public static bool Confirm(Window owner, string title, string message) =>
        MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
