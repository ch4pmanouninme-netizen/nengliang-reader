using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Reader.Desktop;

/// <summary>
/// 轻量对话框工厂：提供文本输入、消息提示与确认三种模态窗口，统一外观与居中行为。
/// </summary>
/// <remarks>所有方法都应在 UI 线程调用；窗口通过 ShowDialog 拥有 owner 的模态输入。</remarks>
internal static class Dialogs
{
    /// <summary>弹出单行文本输入框。</summary>
    /// <param name="owner">拥有该对话框的窗口，决定居中位置与模态范围。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="description">说明文字，同时作为输入框水印。</param>
    /// <param name="initial">输入框初始文本。</param>
    /// <returns>点击「确定」返回输入内容；取消、Esc 或关窗返回 null。</returns>
    public static async Task<string?> Prompt(Window owner, string title, string description, string initial = "")
    {
        var input = new TextBox { Text = initial, Watermark = description };
        var window = Base(title);
        var ok = new Button { Content = "确定", IsDefault = true };
        var cancel = new Button { Content = "取消", IsCancel = true };
        // Close(参数) 的返回值就是 ShowDialog 的结果，null 统一表示用户取消。
        ok.Click += (_, _) => window.Close(input.Text ?? "");
        cancel.Click += (_, _) => window.Close(null);
        window.Content = new StackPanel { Margin = new Thickness(24), Spacing = 14, Children = {
            new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap }, input,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { ok, cancel } } } };
        // 打开后聚焦输入框，避免用户还要先点一下才能输入。
        window.Opened += (_, _) => input.Focus();
        return await window.ShowDialog<string?>(owner);
    }

    /// <summary>显示只有「知道了」按钮的消息提示。</summary>
    /// <param name="owner">拥有该对话框的窗口，决定居中位置与模态范围。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="message">提示正文，自动换行。</param>
    /// <returns>用户关闭对话框后完成。</returns>
    public static async Task Message(Window owner, string title, string message)
    {
        var window = Base(title);
        var close = new Button { Content = "知道了", IsDefault = true, IsCancel = true };
        close.Click += (_, _) => window.Close();
        window.Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = {
            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, close } };
        await window.ShowDialog(owner);
    }

    /// <summary>显示确认对话框。</summary>
    /// <param name="owner">拥有该对话框的窗口，决定居中位置与模态范围。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="message">确认正文，自动换行。</param>
    /// <param name="confirmText">确认按钮文字。</param>
    /// <param name="cancelText">取消按钮文字。</param>
    /// <returns>确认返回 true；取消、Esc 或关窗返回 false。</returns>
    /// <remarks>「取消」是默认按钮，避免回车误触删除。</remarks>
    public static async Task<bool> Confirm(Window owner, string title, string message, string confirmText = "删除", string cancelText = "取消")
    {
        var window = Base(title); window.Name = "ConfirmationDialog";
        // 按钮内容使用可换行的 TextBlock，长文案不会把固定宽度的对话框撑开。
        var confirm = new Button { Name = "Confirm", Content = new TextBlock { Text = confirmText, MaxWidth = 190, TextWrapping = TextWrapping.Wrap } };
        var cancel = new Button { Name = "Cancel", Content = new TextBlock { Text = cancelText, MaxWidth = 190, TextWrapping = TextWrapping.Wrap }, IsDefault = true, IsCancel = true };
        confirm.Click += (_, _) => window.Close(true); cancel.Click += (_, _) => window.Close(false);
        window.Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = {
            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { cancel, confirm } } } };
        return await window.ShowDialog<bool>(owner);
    }

    // 三种对话框共用固定宽度和随内容变化的高度，避免长文本撑出屏幕。
    private static Window Base(string title) => new() { Title = title, Icon = AppIcon.Shared, Width = 500, SizeToContent = SizeToContent.Height,
        MinHeight = 180, MaxHeight = 650, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
}
