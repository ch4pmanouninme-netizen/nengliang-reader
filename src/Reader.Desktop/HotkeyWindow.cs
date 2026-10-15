using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Reader.Desktop;

// 全局快捷键设置：每个功能一行，逐行实时校验，全部合法才允许保存。
internal sealed class HotkeyWindow : Window
{
    private readonly TextBox[] boxes = new TextBox[Hotkeys.Functions.Length];
    private readonly TextBlock[] errors = new TextBlock[Hotkeys.Functions.Length];
    private readonly Button save = new() { Name = "SaveHotkeys", Content = "保存", IsDefault = true, IsEnabled = false };
    public string[]? Result { get; private set; }

    public HotkeyWindow(string[] keys)
    {
        Name = "HotkeyDialog";
        Title = "全局快捷键"; Width = 520; SizeToContent = SizeToContent.Height; MinHeight = 260; MaxHeight = 650;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var rows = new StackPanel { Margin = new Thickness(24), Spacing = 10 };
        rows.Children.Add(new TextBlock { Text = $"固定搭配 {Hotkeys.Modifier}；为每个功能输入一个按键。", TextWrapping = TextWrapping.Wrap });
        rows.Children.Add(new TextBlock { Text = "支持 A–Z、F1–F12、Left、Right、Space，五个键互不相同。", FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*,140") };
        for (int index = 0; index < boxes.Length; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = Hotkeys.Functions[index], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            var box = new TextBox { Name = "Key" + index, Text = keys[index], MaxLength = 8, Margin = new Thickness(0, 4, 12, 4) };
            var error = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.OrangeRed, Margin = new Thickness(0, 4, 0, 4) };
            boxes[index] = box; errors[index] = error;
            // PropertyChanged rather than TextChanged: the latter only fires once the box is attached
            // to a visual tree, which would leave validation dead until the window is shown.
            box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Validate(); };
            Grid.SetRow(label, index); Grid.SetColumn(label, 0);
            Grid.SetRow(box, index); Grid.SetColumn(box, 1);
            Grid.SetRow(error, index); Grid.SetColumn(error, 2);
            grid.Children.Add(label); grid.Children.Add(box); grid.Children.Add(error);
        }
        rows.Children.Add(grid);
        var cancel = new Button { Name = "CancelHotkeys", Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        save.Click += (_, _) =>
        {
            string?[] names = boxes.Select(box => Hotkeys.Canonical(box.Text ?? "")).ToArray();
            if (names.Any(name => name == null)) return;
            Result = names!;
            Close(true);
        };
        rows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { save, cancel } });
        Content = rows;
        Validate();
    }

    private void Validate()
    {
        string?[] names = boxes.Select(box => Hotkeys.Canonical(box.Text ?? "")).ToArray();
        bool valid = true;
        for (int index = 0; index < names.Length; index++)
        {
            string message = names[index] == null ? "无效按键"
                : names.Count(name => name == names[index]) > 1 ? "与其他行重复" : "";
            errors[index].Text = message;
            if (message.Length > 0) valid = false;
        }
        save.IsEnabled = valid;
    }
}
