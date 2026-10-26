using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Reader.Core;

namespace Reader.Desktop;

/// <summary>
/// 界面主题目录与应用逻辑：定义内置主题、读取随包官方主题，并把调色板和控件资源写入 Avalonia 主题。
/// </summary>
/// <remarks>Apply 必须在 UI 线程调用；编辑器资源字典会原地清空复用，避免反复合并字典。</remarks>
internal static class UiThemes
{
    /// <summary>一个可选界面主题及其调色板。</summary>
    /// <param name="Id">写入偏好文件的稳定标识。</param>
    /// <param name="Name">下拉框中显示的名称。</param>
    /// <param name="Dark">是否使用深色变体。</param>
    /// <param name="Background">窗口背景色。</param>
    /// <param name="Accent">强调色。</param>
    /// <param name="Surface">输入框和面板的表面色。</param>
    /// <param name="Foreground">正文前景色。</param>
    internal sealed record Choice(string Id, string Name, bool Dark, string Background, string Accent, string Surface = "#35383E", string Foreground = "#D8DEE9")
    {
        /// <summary>下拉框分组名；内置主题固定为「自定义」。</summary>
        public string Group { get; init; } = "自定义";
        /// <summary>阅读条正文颜色。</summary>
        public string EditorForeground { get; init; } = "#D8DEE9";
        /// <summary>输入框文字颜色。</summary>
        public string InputForeground { get; init; } = "#D8DEE9";
        /// <summary>按钮和输入框边框颜色。</summary>
        public string Border { get; init; } = "#555555";
        /// <summary>按钮默认背景色。</summary>
        public string Button { get; init; } = "#35383E";
        /// <summary>按钮文字颜色。</summary>
        public string ButtonForeground { get; init; } = "#FFFFFF";
        /// <summary>按钮悬停/按下背景色。</summary>
        public string ButtonHover { get; init; } = "#454850";
        /// <summary>菜单背景色。</summary>
        public string Menu { get; init; } = "#35383E";
        /// <summary>菜单文字颜色。</summary>
        public string MenuForeground { get; init; } = "#FFFFFF";
        /// <summary>菜单边框颜色。</summary>
        public string MenuBorder { get; init; } = "#555555";
        /// <summary>文本选中背景色。</summary>
        public string Selection { get; init; } = "#3574F0";
        /// <summary>文本选中前景色。</summary>
        public string SelectionForeground { get; init; } = "#FFFFFF";
        /// <summary>标题栏背景色；内置主题不使用。</summary>
        public string Header { get; init; } = "#35383E";
        /// <summary>标题栏文字颜色；内置主题不使用。</summary>
        public string HeaderForeground { get; init; } = "#FFFFFF";
        /// <summary>官方主题来源说明；内置主题为空串。</summary>
        public string Source { get; init; } = "";
    }
    /// <summary>随程序打包的官方主题目录，来自嵌入的 JSON 资源。</summary>
    internal static readonly Choice[] Official = LoadOfficial();
    // 官方主题放在嵌入资源里，避免运行时依赖外部文件；反序列化失败直接视为打包错误。
    private static Choice[] LoadOfficial()
    {
        using var source = typeof(UiThemes).Assembly.GetManifestResourceStream("Reader.Desktop.OfficialThemes.json")
            ?? throw new InvalidOperationException("Missing official theme catalog");
        return System.Text.Json.JsonSerializer.Deserialize<Choice[]>(source)!;
    }
    /// <summary>全部可选主题：内置主题在前，官方主题追加在后。</summary>
    internal static readonly Choice[] Choices =
    [
        new("light", "清爽浅色", false, "#F5F7FA", "#2563EB"),
        new("dark", "柔和深灰", true, "#303238", "#93B4FF", "#3B3E45"),
        new("mint", "薄荷绿", false, "#EAF5EF", "#16704A"),
        new("sky", "天空蓝", false, "#EAF3FC", "#1768AA"),
        new("lavender", "薰衣草紫", false, "#F2EDFA", "#7652AD"),
        new("apricot", "暖杏", false, "#FFF2E4", "#A65323"),
        new("midnight", "午夜蓝", true, "#253247", "#8CC8FF", "#303F55"),
        new("one-dark", "One Dark · 蓝灰", true, "#282C34", "#61AFEF", "#343A46", "#ABB2BF"),
        .. Official
    ];

    /// <summary>按偏好解析主题；未知 Id 回退到浅色/深色默认项。</summary>
    /// <param name="preferences">包含 UiTheme 与 UiDark 的偏好。</param>
    /// <returns>匹配的主题；找不到时按明暗标记回退到第 1 或第 2 个内置主题。</returns>
    internal static Choice Resolve(Preferences preferences) =>
        Choices.FirstOrDefault(x => x.Id == preferences.UiTheme) ?? Choices[preferences.UiDark ? 1 : 0];

    /// <summary>把主题调色板和控件资源应用到当前应用。</summary>
    /// <param name="preferences">用于解析目标主题的偏好。</param>
    /// <remarks>需在 UI 线程调用；会替换 Fluent 调色板并清空编辑器的动态资源字典。</remarks>
    internal static void Apply(Preferences preferences)
    {
        if (Application.Current is not App app) return;
        var choice = Resolve(preferences);
        var variant = choice.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var palette = new ColorPaletteResources
        {
            Accent = Color.Parse(choice.Accent), RegionColor = Color.Parse(choice.Background)
        };
        // 深色主题需要额外的 Fluent 色阶，否则默认浅色资源会透出灰白底色。
        if (choice.Dark)
        {
            palette.AltHigh = Color.Parse(choice.Background);
            palette.ChromeLow = Color.Parse(choice.Background);
            palette.ChromeMediumLow = Color.Parse(choice.Surface);
            palette.ChromeHigh = Color.Parse(choice.Surface);
            palette.BaseHigh = Color.Parse(choice.Foreground);
        }
        app.Fluent.Palettes[variant] = palette;
        ApplyEditorResources(app, choice);
        // 先备好调色板和资源，再切换变体，避免控件用默认配色重绘一次。
        app.RequestedThemeVariant = variant;
    }

    // 编辑器控件使用一套独立的动态资源；切换到官方主题时整体替换，回到内置主题则清空。
    private static readonly ResourceDictionary editorResources = new();
    // 官方主题的配色按 Avalonia Fluent 资源键逐项覆盖；内置主题只设置窗口区域和标题栏。
    private static void ApplyEditorResources(App app, Choice choice)
    {
        // Explicit Fluent control resources avoid mixing source colors with translucent defaults.
        if (!app.Resources.MergedDictionaries.Contains(editorResources)) app.Resources.MergedDictionaries.Add(editorResources);
        // 复用同一个字典：清空再填，已合并到应用资源的引用不会失效。
        editorResources.Clear();
        void HeaderBrush(string key, string color) => editorResources[key] = new SolidColorBrush(Color.Parse(color));
        HeaderBrush("ReaderHeaderBackground", choice.Source == "" ? choice.Background : choice.Header);
        HeaderBrush("ReaderHeaderForeground", choice.Source == "" ? (choice.Dark ? choice.Foreground : "#202020") : choice.HeaderForeground);
        // 内置主题没有完整调色板，只覆盖区域背景就返回；官方主题继续覆盖按钮、输入框和菜单。
        if (choice.Source == "") { HeaderBrush("SystemRegionBrush", choice.Background); return; }
        string border = choice.Border;
        string button = choice.Button;
        string buttonText = choice.ButtonForeground;
        string hover = choice.ButtonHover;
        void Brush(string key, string color) => editorResources[key] = new SolidColorBrush(Color.Parse(color));
        Brush("SystemRegionBrush", choice.Background);
        Brush("SystemControlBackgroundAltHighBrush", choice.Background);
        Brush("SystemControlForegroundBaseHighBrush", choice.Foreground);
        foreach (string state in new[] { "", "PointerOver", "Pressed" })
        {
            Brush("ButtonBackground" + state, state == "" ? button : hover);
            Brush("ButtonForeground" + state, buttonText);
            Brush("ButtonBorderBrush" + state, border);
        }
        foreach (string state in new[] { "", "PointerOver", "Focused" })
        {
            Brush("TextControlBackground" + state, choice.Surface);
            Brush("TextControlForeground" + state, choice.InputForeground);
            Brush("TextControlBorderBrush" + state, state == "Focused" ? choice.Accent : border);
        }
        Brush("TextControlSelectionHighlightColor", choice.Selection);
        Brush("MenuFlyoutPresenterBackground", choice.Menu);
        Brush("MenuFlyoutPresenterBorderBrush", choice.MenuBorder);
        Brush("MenuFlyoutItemForeground", choice.MenuForeground);
        Brush("MenuFlyoutItemBackgroundPointerOver", choice.Selection);
        Brush("MenuFlyoutItemForegroundPointerOver", choice.SelectionForeground);
        foreach (string state in new[] { "", "PointerOver", "Pressed" })
        {
            Brush("ComboBoxBackground" + state, choice.Surface);
            Brush("ComboBoxForeground" + state, choice.InputForeground);
            Brush("ComboBoxBorderBrush" + state, choice.Border);
        }
    }

    /// <summary>把控件包进带主题背景的标题栏容器。</summary>
    /// <param name="content">标题栏内容（通常是按钮组）。</param>
    /// <returns>使用动态主题资源的边框。</returns>
    internal static Border Header(Control content)
    {
        var border = new Border { Child = content, Padding = new Thickness(8) };
        border.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ReaderHeaderBackground"));
        content.Bind(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("ReaderHeaderForeground"));
        return border;
    }

    /// <summary>创建主题切换按钮及其下拉菜单。</summary>
    /// <param name="preferences">选中主题后立即写入该偏好。</param>
    /// <param name="changed">主题变化后的回调，用于立即应用主题。</param>
    /// <returns>带下拉菜单的按钮。</returns>
    /// <remarks>选择会立即修改偏好并触发回调；调用方需要自行决定是否持久化。</remarks>
    internal static Button CreatePicker(Preferences preferences, Action changed)
    {
        var button = new Button { Content = "切换主题" };
        var menu = new MenuFlyout();
        var items = new List<(Choice Choice, MenuItem Item)>();
        // 「自定义」内置主题排在最后，官方主题按清单顺序分组展示。
        foreach (var group in Choices.GroupBy(x => x.Group).OrderBy(x => x.Key == "自定义" ? 1 : 0))
        {
            var parent = new MenuItem { Header = group.Key };
            foreach (var choice in group)
            {
                var item = new MenuItem { Header = choice.Name, ToggleType = MenuItemToggleType.Radio, GroupName = "UiTheme", StaysOpenOnClick = false };
                item.Click += (_, _) =>
                {
                    preferences.UiTheme = choice.Id; preferences.UiDark = choice.Dark; changed();
                };
                parent.Items.Add(item); items.Add((choice, item));
            }
            menu.Items.Add(parent);
        }
        // 每次打开都按当前偏好同步勾选状态，避免偏好被别处修改后菜单仍显示旧选项。
        menu.Opening += (_, _) =>
        {
            var selected = Resolve(preferences);
            foreach (var pair in items) pair.Item.IsChecked = pair.Choice == selected;
        };
        button.Flyout = menu;
        return button;
    }
}

