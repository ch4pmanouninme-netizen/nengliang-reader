using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Reader.Desktop;

internal static class Dialogs
{
    public static async Task<string?> Prompt(Window owner, string title, string description, string initial = "")
    {
        var input = new TextBox { Text = initial, Watermark = description };
        var window = Base(title);
        var ok = new Button { Content = "确定", IsDefault = true };
        var cancel = new Button { Content = "取消", IsCancel = true };
        ok.Click += (_, _) => window.Close(input.Text ?? "");
        cancel.Click += (_, _) => window.Close(null);
        window.Content = new StackPanel { Margin = new Thickness(24), Spacing = 14, Children = {
            new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap }, input,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { ok, cancel } } } };
        window.Opened += (_, _) => input.Focus();
        return await window.ShowDialog<string?>(owner);
    }

    public static async Task Message(Window owner, string title, string message)
    {
        var window = Base(title);
        var close = new Button { Content = "知道了", IsDefault = true, IsCancel = true };
        close.Click += (_, _) => window.Close();
        window.Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = {
            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, close } };
        await window.ShowDialog(owner);
    }

    private static Window Base(string title) => new() { Title = title, Width = 500, SizeToContent = SizeToContent.Height,
        MinHeight = 180, MaxHeight = 650, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
}
