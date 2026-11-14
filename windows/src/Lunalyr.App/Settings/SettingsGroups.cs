using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Lunalyr.App.Settings;

/// <summary>
/// Tester feedback on 0.9.0 (item 11): Settings was too much to scroll through. Each page's sections become groups that
/// start collapsed, so related settings stay together and a page reads as a short list of headings. Done once, after a
/// page is built: every top-level section heading (<see cref="SettingsKit.Section"/>) and the rows under it, up to the
/// next heading, go into one Expander. What comes before the first heading (the preview, Show lyrics) stays open. A group
/// is hidden while its heading is, or while every row in it is.
/// </summary>
internal static class SettingsGroups
{
    public static UIElement Fold(UIElement built)
    {
        if (built is not StackPanel page || !Equals(page.Tag, SettingsKit.PageRoot)) return built;
        FoldPanel(page, depth: 0);
        return built;
    }

    /// <summary>
    /// Folds <paramref name="panel"/>'s sections, and those of the panels inside it that are shown for one choice (the
    /// Wallpaper page's per-type sections), a couple of levels down.
    /// </summary>
    private static void FoldPanel(StackPanel panel, int depth)
    {
        if (depth < 3)
            foreach (var nested in panel.Children.OfType<StackPanel>().ToList())
                if (!ReferenceEquals(nested.Tag, SettingsKit.PageRoot)) FoldPanel(nested, depth + 1);
        var children = panel.Children.ToList();
        if (!children.Any(IsHeading)) return;
        var page = panel;
        page.Children.Clear();
        var groups = new List<(TextBlock Heading, StackPanel Rows)>();
        var order = new List<object>(); // a loose element, or a group's index
        foreach (var child in children)
        {
            if (child is TextBlock heading && IsHeading(heading))
            {
                groups.Add((heading, new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch }));
                order.Add(groups.Count - 1);
                continue;
            }
            if (groups.Count == 0) order.Add(child);
            else groups[^1].Rows.Children.Add(child);
        }
        foreach (var item in order)
            page.Children.Add(item is int index ? Group(groups[index].Heading, groups[index].Rows, first: page.Children.Count == 0) : (UIElement)item);
    }

    private static bool IsHeading(UIElement element) =>
        element is TextBlock text && AutomationProperties.GetHeadingLevel(text) == AutomationHeadingLevel.Level2;

    /// <summary>One group, made once its rows are in it, following its heading's and rows' visibility from then on.</summary>
    private static Expander Group(TextBlock heading, StackPanel rows, bool first)
    {
        var expander = SettingsKit.Expander(heading.Text, null, null, rows, expanded: false);
        expander.Margin = new Thickness(0, first ? 0 : 6, 0, 0);
        void Follow()
        {
            var anyRow = false;
            foreach (var row in rows.Children) anyRow |= row.Visibility == Visibility.Visible;
            expander.Visibility = heading.Visibility == Visibility.Visible && anyRow ? Visibility.Visible : Visibility.Collapsed;
        }
        // The heading and the rows keep their own ShowWhen bindings; the group follows them.
        heading.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Follow());
        foreach (var row in rows.Children) row.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Follow());
        Follow();
        return expander;
    }
}
