using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Reader.Core;

namespace Reader.Desktop;

// 良子小窗设置：主题色与主页关闭行为。阅读相关设置仍在阅读设置窗口。
/// <summary>
/// 主页设置窗口：选择界面主题和关闭主页时的行为，并把结果写回偏好。
/// </summary>
/// <remarks>主题选择器只修改偏好，保存后才由主页应用；取消或直接关窗时恢复打开前的主题快照。</remarks>
internal sealed class HomeSettingsWindow : Window
{
    /// <summary>创建主页设置窗口。</summary>
    /// <param name="preferences">应用偏好；保存时直接修改其中的退出方式和主题字段。</param>
    public HomeSettingsWindow(Preferences preferences)
    {
        // 名称供冒烟测试按窗口名定位这个模态对话框。
        Name = "HomeSettingsDialog";
        Title = "良子小窗设置"; Icon = AppIcon.Shared;
        Width = 420; SizeToContent = SizeToContent.Height; MinHeight = 200; MaxHeight = 650;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var rows = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        rows.Children.Add(UiThemes.CreatePicker(preferences, () => { }));
        var tray = new RadioButton { Name = "ExitToTray", GroupName = "ExitMode", Content = "关闭主页时最小化到托盘", IsChecked = preferences.ExitMode == Preferences.ExitToTray };
        var quit = new RadioButton { Name = "ExitProgram", GroupName = "ExitMode", Content = "关闭主页时完全退出程序", IsChecked = preferences.ExitMode == Preferences.ExitProgram };
        var ask = new TextBlock { Text = "尚未选择：第一次关闭主页时会询问并记住选择。", FontSize = 12, IsVisible = preferences.ExitMode == Preferences.ExitUnset };
        rows.Children.Add(new TextBlock { Text = "退出选项" });
        rows.Children.Add(tray); rows.Children.Add(quit); rows.Children.Add(ask);
        // 主题选择器会立即修改 Preferences；取消或直接关窗时恢复快照，避免被下一次保存带走。
        string originalTheme = preferences.UiTheme;
        bool originalDark = preferences.UiDark;
        bool saved = false;
        var save = new Button { Name = "SaveHomeSettings", Content = "保存设置", IsDefault = true };
        var cancel = new Button { Name = "CancelHomeSettings", Content = "取消", IsCancel = true };
        // 退出方式只在保存时写回；取消则完全不动偏好。
        save.Click += (_, _) =>
        {
            preferences.ExitMode = tray.IsChecked == true ? Preferences.ExitToTray : quit.IsChecked == true ? Preferences.ExitProgram : Preferences.ExitUnset;
            saved = true;
            Close(true);
        };
        cancel.Click += (_, _) => Close(false);
        Closing += (_, _) =>
        {
            if (!saved) { preferences.UiTheme = originalTheme; preferences.UiDark = originalDark; }
        };
        rows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { save, cancel } });
        Content = rows;
    }
}
