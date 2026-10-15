using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Reader.Desktop;

// 全局快捷键设置：点中一行后按住 Ctrl+Alt 按键录入。
// 每行自带「重置」按钮（恢复该行默认键），输入框末尾的 ✕ 清除该行绑定。
internal sealed class HotkeyWindow : Window
{
    private readonly TextBox[] boxes = new TextBox[Hotkeys.Functions.Length];
    private readonly TextBlock[] errors = new TextBlock[Hotkeys.Functions.Length];
    private readonly Button save = new() { Name = "SaveHotkeys", Content = "保存", IsDefault = true, IsEnabled = false };
    private int active;
    private string hint = "";
    public string[]? Result { get; private set; }

    public HotkeyWindow(string[] keys)
    {
        Name = "HotkeyDialog";
        Title = "全局快捷键"; Width = 600; SizeToContent = SizeToContent.Height; MinHeight = 260; MaxHeight = 650;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var rows = new StackPanel { Margin = new Thickness(24), Spacing = 10 };
        rows.Children.Add(new TextBlock { Text = $"先点中一行，再按住 {Hotkeys.Modifier} 按下要用的键。", TextWrapping = TextWrapping.Wrap });
        rows.Children.Add(new TextBlock { Text = "支持 A–Z、F1–F12、Left、Right、Space，五个键互不相同；✕ 清除该行绑定，该功能不再有全局快捷键。", FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*,Auto,150") };
        for (int index = 0; index < boxes.Length; index++)
        {
            int row = index;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = Hotkeys.Functions[index], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            var box = new TextBox { Name = "Key" + index, Text = keys[index], IsReadOnly = true, Watermark = "未绑定", Margin = new Thickness(0, 4, 8, 4) };
            var clear = new Button
            {
                Name = "Clear" + index, Content = "✕", Width = 20, Height = 20, Padding = new Thickness(0), FontSize = 11,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand)
            };
            box.InnerRightContent = clear;
            var reset = new Button { Name = "Reset" + index, Content = "重置", Margin = new Thickness(0, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
            var error = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            boxes[index] = box; errors[index] = error;
            // PropertyChanged rather than TextChanged: the latter only fires once the box is attached
            // to a visual tree, which would leave validation dead until the window is shown.
            box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refresh(); };
            box.GotFocus += (_, _) => Select(row);
            box.PointerPressed += (_, _) => { Select(row); box.Focus(); };
            // Acting on a row also selects it, so a following key press lands on the row just clicked.
            clear.Click += (_, _) => { Select(row); Clear(row); };
            reset.Click += (_, _) => { Select(row); Reset(row); };
            Grid.SetRow(label, index); Grid.SetColumn(label, 0);
            Grid.SetRow(box, index); Grid.SetColumn(box, 1);
            Grid.SetRow(reset, index); Grid.SetColumn(reset, 2);
            Grid.SetRow(error, index); Grid.SetColumn(error, 3);
            grid.Children.Add(label); grid.Children.Add(box); grid.Children.Add(reset); grid.Children.Add(error);
        }
        rows.Children.Add(grid);
        var cancel = new Button { Name = "CancelHotkeys", Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        save.Click += (_, _) =>
        {
            string?[] names = boxes.Select(box => Hotkeys.Normalize(box.Text ?? "")).ToArray();
            if (names.Any(name => name == null)) return;
            Result = names!;
            Close(true);
        };
        rows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { save, cancel } });
        Content = rows;
        // Keys are read on the window, not per box. Focus sits on the window itself until a row is
        // clicked, and a tunnelling handler on a box never runs while that box is not in the route —
        // which is exactly the case on open, so per-box handlers received nothing at all.
        AddHandler(InputElement.KeyDownEvent, (_, e) => Capture(e), RoutingStrategies.Tunnel);
        Opened += (_, _) => boxes[active].Focus();
        Refresh();
    }

    internal void Select(int row)
    {
        if (row == active && hint.Length == 0) return;
        active = row; hint = "";
        Refresh();
    }

    // 清除该行绑定：该功能不再注册全局快捷键。
    internal void Clear(int row) => Set(row, "");
    // 重置该行为默认按键，不影响其它行。
    internal void Reset(int row) => Set(row, Hotkeys.Defaults[row]);

    private void Set(int row, string key)
    {
        hint = "";
        boxes[row].Text = key;
        Refresh();
    }

    // 录入：按住 Ctrl+Alt 再按键。Esc 放行以便关闭窗口；修饰键与不支持的键不改动原值。
    private void Capture(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) return;
        string? name = Hotkeys.NameOf(e.Key);
        if (name == null) return;
        e.Handled = true;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != (KeyModifiers.Control | KeyModifiers.Alt))
        {
            hint = $"需同时按住 {Hotkeys.Modifier}";
            Refresh();
            return;
        }
        hint = "";
        boxes[active].Text = name;
        Refresh();
    }

    private void Refresh()
    {
        string?[] names = boxes.Select(box => Hotkeys.Normalize(box.Text ?? "")).ToArray();
        bool valid = true;
        for (int index = 0; index < names.Length; index++)
        {
            // 空串是「未绑定」，不是重复，也不影响保存。
            string invalid = names[index] == null ? "无效按键"
                : names[index]!.Length > 0 && names.Count(name => name == names[index]) > 1 ? "与其他行重复" : "";
            if (invalid.Length > 0) valid = false;
            errors[index].Text = invalid.Length > 0 ? invalid
                : index == active ? (hint.Length > 0 ? hint : $"按 {Hotkeys.Modifier} + 键") : "";
            errors[index].Foreground = invalid.Length > 0 ? Brushes.OrangeRed : Brushes.Gray;
        }
        save.IsEnabled = valid;
    }
}
