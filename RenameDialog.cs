using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UsbIpClient;

/** Small prompt for a device label. Returns the text, "" to reset to the automatic name, or null if cancelled. */
public sealed class RenameDialog : Window
{
    private readonly TextBox _box;

    private RenameDialog(Window owner, string current, string autoName)
    {
        Owner = owner;
        Title = "Rename device";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _box = new TextBox { Text = current, MinWidth = 340, Margin = new Thickness(0, 8, 0, 0) };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { DialogResult = true; }
            if (e.Key == Key.Escape) { DialogResult = false; }
        };
        var ok = new Button { Content = "Save", MinWidth = 80, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => DialogResult = true;
        if (TryFindResource("AccentButtonStyle") is Style accent) ok.Style = accent;
        var reset = new Button { Content = "Use automatic name", Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) => { _box.Text = ""; DialogResult = true; };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Give this device a name you'll recognize." });
        panel.Children.Add(new TextBlock { Text = $"Automatic name: {autoName}", Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window owner, string current, string autoName)
    {
        var d = new RenameDialog(owner, current, autoName);
        return d.ShowDialog() == true ? d._box.Text : null;
    }
}
