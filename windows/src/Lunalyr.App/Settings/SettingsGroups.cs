using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Lunalyr.App.Settings;

/// <summary>
/// The owner's Settings reorganisation (15 Nov 2026): every section stays open and shows the settings most people change;
/// the rest of a section sit in one closed "Advanced …" expander at its end ("Advanced layout" under Layout). Pages mark
/// those rows with <see cref="Advanced{T}"/> as they are built; once a page is built, each top-level section heading
/// (<see cref="SettingsKit.Section"/>), its rows up to the next heading and that expander are gathered into one panel,
/// which hides while its heading is hidden or none of its rows shows. This replaced 0.9.1's groups that all started
/// collapsed (tester feedback item 11), which made every page a column of closed headings.
/// </summary>
internal static class SettingsGroups
{
    private static readonly ConditionalWeakTable<UIElement, object> Marked = new();
    private static readonly object Mark = new();

    /// <summary>Puts <paramref name="row"/> in its section's Advanced expander.</summary>
    public static T Advanced<T>(T row) where T : UIElement
    {
        Marked.AddOrUpdate(row, Mark);
        return row;
    }

    /// <summary>Puts every row added to <paramref name="panel"/> since <paramref name="start"/> in the Advanced expander.</summary>
    public static void AdvancedFrom(Panel panel, int start)
    {
        for (var i = start; i < panel.Children.Count; i++) Advanced(panel.Children[i]);
    }

    public static bool IsAdvanced(UIElement row) => Marked.TryGetValue(row, out _);

    public static UIElement Fold(UIElement built)
    {
        if (built is not StackPanel page || !Equals(page.Tag, SettingsKit.PageRoot)) return built;
        FoldPanel(page, depth: 0);
        return built;
    }

    /// <summary>
    /// Gathers <paramref name="panel"/>'s sections, and those of the panels inside it that are shown for one choice (the
    /// Wallpaper page's per-type sections, the floating lyrics), a couple of levels down.
    /// </summary>
    private static void FoldPanel(StackPanel panel, int depth)
    {
        if (depth < 3)
            foreach (var nested in panel.Children.OfType<StackPanel>().ToList())
                if (!IsAdvanced(nested)) FoldPanel(nested, depth + 1);
        var children = panel.Children.ToList();
        if (!children.Any(IsHeading) && !children.Any(IsAdvanced)) return;
        panel.Children.Clear();
        var loose = new Section(null);
        Section current = loose;
        var sections = new List<Section>();
        foreach (var child in children)
        {
            if (child is TextBlock heading && IsHeading(heading))
            {
                current = new Section(heading);
                sections.Add(current);
                continue;
            }
            (IsAdvanced(child) ? current.Extra : current.Rows).Add(child);
        }
        // What comes before the first heading (the preview, Show lyrics, the wallpaper type) stays loose and open.
        foreach (var row in loose.Rows) panel.Children.Add(row);
        if (loose.Extra.Count > 0) panel.Children.Add(Extras(null, loose.Extra));
        foreach (var section in sections) panel.Children.Add(Gather(section));
    }

    private sealed class Section(TextBlock? heading)
    {
        public TextBlock? Heading { get; } = heading;
        public List<UIElement> Rows { get; } = [];
        public List<UIElement> Extra { get; } = [];
    }

    private static bool IsHeading(UIElement element) =>
        element is TextBlock text && AutomationProperties.GetHeadingLevel(text) == AutomationHeadingLevel.Level2;

    /// <summary>One section, made once its rows are in it, following its heading's and rows' visibility from then on.</summary>
    private static StackPanel Gather(Section section)
    {
        var heading = section.Heading!;
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        // A section that is all Advanced is one expander under the section's own name ("Sources", 5 settings) rather
        // than a heading over "Advanced sources"; its heading still decides whether it shows.
        var whole = section.Rows.Count == 0 && section.Extra.Count > 0;
        if (!whole) panel.Children.Add(heading);
        foreach (var row in section.Rows) panel.Children.Add(row);
        var extras = section.Extra.Count > 0 ? Extras(heading.Text, section.Extra, whole) : null;
        if (extras is not null)
        {
            if (whole) extras.Margin = new Thickness(0, heading.Margin.Top, 0, 0);
            panel.Children.Add(extras);
        }
        void Follow()
        {
            var anyRow = section.Rows.Any(Shown) || (extras?.Visibility == Visibility.Visible);
            panel.Visibility = heading.Visibility == Visibility.Visible && anyRow ? Visibility.Visible : Visibility.Collapsed;
        }
        // The heading and the rows keep their own ShowWhen bindings; the section follows them.
        heading.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Follow());
        foreach (var row in section.Rows) row.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Follow());
        extras?.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Follow());
        Follow();
        return panel;
    }

    /// <summary>A section's Advanced expander: closed, hidden while none of its rows shows, and saying how many it holds.</summary>
    private static Expander Extras(string? sectionTitle, List<UIElement> rows, bool whole = false)
    {
        var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var row in rows) content.Children.Add(row);
        var title = whole ? sectionTitle! : AdvancedTitle(sectionTitle);
        var more = whole ? "" : " more";
        var expander = SettingsKit.Expander(title, null, null, content, expanded: false);
        var header = (Card)expander.Header;
        void Follow()
        {
            var shown = rows.Where(Shown).Sum(Settings);
            var any = rows.Any(Shown);
            expander.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            header.Description = shown == 1 ? $"1{more} setting" : shown > 1 ? $"{shown}{more} settings" : "";
            AutomationProperties.SetHelpText(expander, header.Description);
        }
        foreach (var row in rows) row.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Follow());
        Follow();
        return expander;
    }

    /// <summary>"Advanced layout", "Advanced sources"; a name stays capitalised ("Advanced Discord"); "This …" sections get "More for this …".</summary>
    internal static string AdvancedTitle(string? sectionTitle)
    {
        if (string.IsNullOrWhiteSpace(sectionTitle)) return "Advanced";
        var first = sectionTitle.Split(' ')[0];
        // "This song’s video" reads badly after "Advanced": "More for this song’s video".
        if (first == "This") return "More for t" + sectionTitle[1..];
        var keep = first.Length > 1 && (char.IsUpper(first[1]) || Names.Contains(first));
        return "Advanced " + (keep ? sectionTitle : char.ToLowerInvariant(sectionTitle[0]) + sectionTitle[1..]);
    }

    private static readonly HashSet<string> Names = ["Discord", "Custom", "YouTube", "Constellation", "Echo", "Apple", "Windows"];

    private static bool Shown(UIElement row) => row.Visibility == Visibility.Visible;

    /// <summary>How many settings a row holds: one for a card, an expander or a list in a card; a panel of rows (Discord's
    /// options) counts its own; a caption or a notice none.</summary>
    private static int Settings(UIElement row) => row switch
    {
        Card or Expander or Border or Grid => 1,
        StackPanel { Orientation: Orientation.Vertical } panel => panel.Children.Where(Shown).Sum(Settings),
        StackPanel => 1,
        _ => 0,
    };
}
