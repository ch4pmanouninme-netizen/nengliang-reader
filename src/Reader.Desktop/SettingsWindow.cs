using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Reader.Core;

namespace Reader.Desktop;

internal sealed class SettingsWindow : Window
{
    public SettingsWindow(Preferences preferences)
    {
        Title = "阅读设置"; Icon = AppIcon.Shared;
        Width = 470; Height = 610; MinWidth = 400; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var rows = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        string[] legacyThemes = { "浅色", "柔和深灰", "暖纸", "灰色", "企业微信浅色", "IDEA / PyCharm Darcula 风格", "透明文字", "VS Code Dark+ 风格", "One Dark 蓝灰", "柔和午夜蓝", "VS Code · 2026 深色", "PyCharm · 新版 Dark" };
        var officialIndex = Array.FindIndex(UiThemes.Official, x => x.Id == preferences.ReadingThemeId);
        var theme = new ComboBox { ItemsSource = legacyThemes.Concat(UiThemes.Official.Select(x => x.Group + " · " + x.Name)).ToArray(), SelectedIndex = officialIndex >= 0 ? legacyThemes.Length + officialIndex : preferences.Theme, HorizontalAlignment = HorizontalAlignment.Stretch };
        var font = new TextBox { Text = preferences.FontName };
        var size = Number(preferences.FontSize, 9, 32);
        var lines = Number(preferences.LineCount, 1, 10);
        var opacity = Number(preferences.Opacity, 30, 100);
        var seconds = Number(preferences.Seconds, 2, 60);
        var top = new CheckBox { Content = "始终置顶", IsChecked = preferences.TopMost };
        var minimal = new CheckBox { Content = "极简阅读条（隐藏底部提示）", IsChecked = preferences.Stealth };
        Add(rows, "外观", theme); Add(rows, "字体名称（不存在时使用系统回退字体）", font);
        Add(rows, "字号", size); Add(rows, "显示行数", lines); Add(rows, "不透明度 %", opacity); Add(rows, "自动翻页间隔（秒）", seconds);
        rows.Children.Add(top); rows.Children.Add(minimal);
        var save = new Button { Content = "保存设置", IsDefault = true };
        save.Click += (_, _) =>
        {
            if (theme.SelectedIndex < legacyThemes.Length) { preferences.Theme = Math.Max(0, theme.SelectedIndex); preferences.ReadingThemeId = ""; }
            else preferences.ReadingThemeId = UiThemes.Official[theme.SelectedIndex - legacyThemes.Length].Id;
            preferences.FontName = string.IsNullOrWhiteSpace(font.Text) ? "sans-serif" : font.Text.Trim();
            preferences.FontSize = (float)(size.Value ?? 14); preferences.LineCount = (int)(lines.Value ?? 1);
            preferences.Opacity = (int)(opacity.Value ?? 100); preferences.Seconds = (int)(seconds.Value ?? 6);
            preferences.TopMost = top.IsChecked == true; preferences.Stealth = minimal.IsChecked == true;
            if (string.IsNullOrEmpty(preferences.UiTheme)) preferences.UiDark = UiThemes.Official.FirstOrDefault(x => x.Id == preferences.ReadingThemeId)?.Dark ?? (preferences.Theme is 1 or 5 or >= 7);
            Close(true);
        };
        var cancel = new Button { Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        rows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { save, cancel } });
        Content = new ScrollViewer { Content = rows };
    }
    private static NumericUpDown Number(float value, int min, int max) => new() { Value = (decimal)value, Minimum = min, Maximum = max, Increment = 1 };
    private static void Add(StackPanel rows, string label, Control control) { rows.Children.Add(new TextBlock { Text = label }); rows.Children.Add(control); }
}
