using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Reader.Core;

namespace Reader.Desktop;

/// <summary>
/// 「关于」窗口：展示产品名、运行版本、应用图标与项目地址。
/// </summary>
/// <remarks>项目地址使用可选中文本而不是超链接，程序不会主动打开浏览器。</remarks>
internal sealed class AboutWindow : Window
{
    /// <summary>创建「关于」窗口。</summary>
    public AboutWindow()
    {
        // 名称供冒烟测试按窗口名定位这个模态对话框。
        Name = "AboutDialog";
        Title = "关于良子小窗"; Width = 420; SizeToContent = SizeToContent.Height;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppIcon.Shared;
        var rows = new StackPanel { Margin = new Thickness(28), Spacing = 10 };
        rows.Children.Add(new Image
        {
            Source = AppIcon.Image, Width = 96, Height = 96,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6)
        });
        rows.Children.Add(new TextBlock
        {
            Name = "AboutName", Text = "良子小窗", FontSize = 20, FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        rows.Children.Add(new TextBlock
        {
            Name = "AboutVersion", Text = "版本 " + AppInfo.Version, FontSize = 12,
            Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center
        });
        // 可选中复制，但不做链接样式、也不打开浏览器 —— 程序不主动访问外部地址。
        rows.Children.Add(new SelectableTextBlock
        {
            Name = "AboutSource", Text = "https://github.com/ch4pmanouninme-netizen/nengliang-reader/",
            FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        });
        var close = new Button
        {
            Name = "CloseAbout", Content = "关闭", IsDefault = true, IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0)
        };
        close.Click += (_, _) => Close();
        rows.Children.Add(close);
        Content = rows;
    }
}
