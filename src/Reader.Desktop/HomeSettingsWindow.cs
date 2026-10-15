using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Reader.Core;

namespace Reader.Desktop;

// 良子小窗设置：主题色与主页关闭行为。阅读相关设置仍在阅读设置窗口。
internal sealed class HomeSettingsWindow : Window
{
    public HomeSettingsWindow(Preferences preferences)
    {
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
        var save = new Button { Name = "SaveHomeSettings", Content = "保存设置", IsDefault = true };
        var cancel = new Button { Name = "CancelHomeSettings", Content = "取消", IsCancel = true };
        save.Click += (_, _) =>
        {
            preferences.ExitMode = tray.IsChecked == true ? Preferences.ExitToTray : quit.IsChecked == true ? Preferences.ExitProgram : Preferences.ExitUnset;
            Close(true);
        };
        cancel.Click += (_, _) => Close(false);
        rows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { save, cancel } });
        Content = rows;
    }
}
