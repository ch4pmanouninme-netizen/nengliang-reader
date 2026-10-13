using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Reader.Core;

namespace Reader.Desktop;

internal static class UiThemes
{
    internal sealed record Choice(string Id, string Name, bool Dark, string Background, string Accent, string Surface = "#35383E", string Foreground = "#D8DEE9")
    {
        public string Group { get; init; } = "自定义";
        public string EditorForeground { get; init; } = "#D8DEE9";
        public string InputForeground { get; init; } = "#D8DEE9";
        public string Border { get; init; } = "#555555";
        public string Button { get; init; } = "#35383E";
        public string ButtonForeground { get; init; } = "#FFFFFF";
        public string ButtonHover { get; init; } = "#454850";
        public string Menu { get; init; } = "#35383E";
        public string MenuForeground { get; init; } = "#FFFFFF";
        public string MenuBorder { get; init; } = "#555555";
        public string Selection { get; init; } = "#3574F0";
        public string SelectionForeground { get; init; } = "#FFFFFF";
        public string Header { get; init; } = "#35383E";
        public string HeaderForeground { get; init; } = "#FFFFFF";
        public string Source { get; init; } = "";
    }
    internal static readonly Choice[] Official = LoadOfficial();
    private static Choice[] LoadOfficial()
    {
        using var source = typeof(UiThemes).Assembly.GetManifestResourceStream("Reader.Desktop.OfficialThemes.json")
            ?? throw new InvalidOperationException("Missing official theme catalog");
        return System.Text.Json.JsonSerializer.Deserialize<Choice[]>(source)!;
    }
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

    internal static Choice Resolve(Preferences preferences) =>
        Choices.FirstOrDefault(x => x.Id == preferences.UiTheme) ?? Choices[preferences.UiDark ? 1 : 0];

    internal static void Apply(Preferences preferences)
    {
        if (Application.Current is not App app) return;
        var choice = Resolve(preferences);
        var variant = choice.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var palette = new ColorPaletteResources
        {
            Accent = Color.Parse(choice.Accent), RegionColor = Color.Parse(choice.Background)
        };
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
        app.RequestedThemeVariant = variant;
    }

    private static readonly ResourceDictionary editorResources = new();
    private static void ApplyEditorResources(App app, Choice choice)
    {
        // Explicit Fluent control resources avoid mixing source colors with translucent defaults.
        if (!app.Resources.MergedDictionaries.Contains(editorResources)) app.Resources.MergedDictionaries.Add(editorResources);
        editorResources.Clear();
        void HeaderBrush(string key, string color) => editorResources[key] = new SolidColorBrush(Color.Parse(color));
        HeaderBrush("ReaderHeaderBackground", choice.Source == "" ? choice.Background : choice.Header);
        HeaderBrush("ReaderHeaderForeground", choice.Source == "" ? (choice.Dark ? choice.Foreground : "#202020") : choice.HeaderForeground);
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

    internal static Border Header(Control content)
    {
        var border = new Border { Child = content, Padding = new Thickness(8) };
        border.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ReaderHeaderBackground"));
        content.Bind(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("ReaderHeaderForeground"));
        return border;
    }

    internal static Button CreatePicker(Preferences preferences, Action changed)
    {
        var button = new Button { Content = "切换主题" };
        var menu = new MenuFlyout();
        var items = new List<(Choice Choice, MenuItem Item)>();
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
        menu.Opening += (_, _) =>
        {
            var selected = Resolve(preferences);
            foreach (var pair in items) pair.Item.IsChecked = pair.Choice == selected;
        };
        button.Flyout = menu;
        return button;
    }
}

