using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Reader.Desktop;

// 全局快捷键设置：点中一行后直接按下想用的组合键录入。
// 每行自带「重置」按钮（恢复该行默认组合键），输入框末尾的 ✕ 清除该行绑定。
/// <summary>
/// 全局快捷键设置窗口：录入五个功能的组合键，校验重复/缺少修饰键/无效按键，并标注注册失败的行。
/// </summary>
/// <remarks>组合键可自由选择 Ctrl / Alt / Shift / Win（Mac 为 Control / Option / Shift / Command），
/// 但至少需要一个修饰键；空槽位表示该功能不绑定。窗口只负责收集结果，实际注册由 <see cref="Hotkeys"/> 完成。</remarks>
internal sealed class HotkeyWindow : Window
{
    private readonly TextBox[] boxes = new TextBox[Hotkeys.Functions.Length];
    private readonly TextBlock[] errors = new TextBlock[Hotkeys.Functions.Length];
    private readonly Button save = new() { Name = "SaveHotkeys", Content = "保存", IsDefault = true, IsEnabled = false };
    // 从主窗口传入的注册失败标记；编辑对应行后清除。
    private readonly bool[] failed;
    // 当前选中行，键盘录入落到这一行。
    private int active;
    // 当前行的临时提示（例如修饰键不完整）。
    private string hint = "";
    /// <summary>保存后的五个按键；取消或关闭时为 null。</summary>
    public string[]? Result { get; private set; }

    /// <summary>创建快捷键设置窗口。</summary>
    /// <param name="keys">当前按键配置，长度必须与功能列表一致。</param>
    /// <param name="failedRows">各功能是否注册失败；null 表示没有失败信息。</param>
    public HotkeyWindow(string[] keys, bool[]? failedRows = null)
    {
        failed = failedRows is { Length: > 0 } ? failedRows : new bool[keys.Length];
        Name = "HotkeyDialog";
        Title = "全局快捷键"; Icon = AppIcon.Shared;
        Width = 600; SizeToContent = SizeToContent.Height; MinHeight = 260; MaxHeight = 650;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var rows = new StackPanel { Margin = new Thickness(24), Spacing = 10 };
        rows.Children.Add(new TextBlock { Text = "先点中一行，再直接按下想用的组合键（至少包含一个修饰键）。", TextWrapping = TextWrapping.Wrap });
        rows.Children.Add(new TextBlock { Text = "支持 A–Z、0–9、小键盘 0–9、F1–F12、方向键、Home/End/PageUp/PageDown、Space；组合键互不相同。全局快捷键必须包含一个非修饰键，系统接口也不区分左右 Ctrl/Alt/Shift。✕ 清除该行绑定。", FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
        // 每行固定四列：功能名、只读输入框、重置按钮、错误/提示。
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*,Auto,150") };
        for (int index = 0; index < boxes.Length; index++)
        {
            int row = index;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = Hotkeys.Functions[index], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            var box = new TextBox { Name = "Key" + index, Text = Hotkeys.Display(keys[index]), IsReadOnly = true, Watermark = "未绑定", Margin = new Thickness(0, 4, 8, 4) };
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
            // 改过值的那一行不再沿用旧的注册结果，否则会挂着一个已经过期的「注册失败」。
            box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) { failed[row] = false; Refresh(); } };
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
        // 保存前再归一化一次；任一行无效或重复时保持窗口打开。
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
        // 打开后把焦点放到当前行，键盘录入无需先点击。
        Opened += (_, _) => boxes[active].Focus();
        Refresh();
    }

    // 切换当前行并清空上一行遗留的提示。
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
        boxes[row].Text = Hotkeys.Display(key);
        Refresh();
    }

    // 录入完整组合键。Esc 放行以便关闭窗口；单独的修饰键、无修饰键或不受支持的键只提示，不改原值。
    private void Capture(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) return;
        // 修饰键本身不是主键；系统热键接口也不支持仅由修饰键组成的组合。
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
        {
            hint = "还需要一个非修饰键（系统不支持仅修饰键组合）";
            Refresh();
            return;
        }
        string? name = Hotkeys.NameOf(e.Key);
        if (name == null) { hint = "不支持的按键"; Refresh(); return; }
        string? gesture = Hotkeys.FromInput(name, e.KeyModifiers);
        if (gesture == null) { hint = "至少需要一个修饰键"; Refresh(); return; }
        e.Handled = true;
        hint = "";
        boxes[active].Text = Hotkeys.Display(gesture);
        Refresh();
    }

    // 统一校验所有行：无效/重复立即报错，注册失败只在未修改的行显示。
    private void Refresh()
    {
        string?[] names = boxes.Select(box => Hotkeys.Normalize(box.Text ?? "")).ToArray();
        bool valid = true;
        for (int index = 0; index < names.Length; index++)
        {
            // 空串是「未绑定」，不是重复，也不影响保存。
            string invalid = names[index] == null ? "无效组合键"
                : names[index]!.Length > 0 && names.Count(name => name == names[index]) > 1 ? "与其他行重复" : "";
            if (invalid.Length > 0) valid = false;
            bool unregistered = invalid.Length == 0 && failed[index];
            errors[index].Text = invalid.Length > 0 ? invalid
                : unregistered ? "注册失败（可能被其他程序占用）"
                : index == active ? (hint.Length > 0 ? hint : "按下组合键") : "";
            errors[index].Foreground = invalid.Length > 0 || unregistered ? Brushes.OrangeRed : Brushes.Gray;
        }
        save.IsEnabled = valid;
    }
}
