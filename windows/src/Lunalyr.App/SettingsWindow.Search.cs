using Lunalyr.App.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Lunalyr.App;

// "Find a setting" (the owner's Settings reorganisation, 15 Nov 2026): a search box at the top of the pane. Typing lists
// the settings whose names match, as plain text ("Video blur · Wallpaper"); choosing one opens its page, opens the
// Advanced expander it sits in and brings it into view. The pages are built the first time something is typed, not
// when the window opens, so the loader and the live preview start as before. Only settings showing for the current
// choices are offered (a Music video row while Music video is the wallpaper type).
public sealed partial class SettingsWindow
{
    private sealed record SearchHit(string Title, string Page, FrameworkElement Target, IReadOnlyList<UIElement> Path,
                                    IReadOnlyList<Expander> Expanders);

    private const string NoMatch = "No settings found";
    private List<SearchHit>? _searchIndex;
    private readonly Dictionary<string, SearchHit> _suggested = [];

    private void SetUpSearch()
    {
        var box = new AutoSuggestBox { PlaceholderText = "Find a setting", QueryIcon = new SymbolIcon(Symbol.Find) };
        AutomationProperties.SetName(box, "Find a setting");
        box.TextChanged += (sender, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var query = sender.Text.Trim();
            _suggested.Clear();
            if (query.Length < 2)
            {
                sender.ItemsSource = null;
                return;
            }
            var items = new List<string>();
            foreach (var hit in Matches(query))
            {
                var text = $"{hit.Title} · {hit.Page}";
                if (_suggested.TryAdd(text, hit)) items.Add(text);
                if (items.Count == 12) break;
            }
            sender.ItemsSource = items.Count > 0 ? items : new List<string> { NoMatch };
        };
        box.QuerySubmitted += (sender, e) =>
        {
            var chosen = e.ChosenSuggestion as string;
            var hit = chosen is not null && _suggested.TryGetValue(chosen, out var picked) ? picked
                : Matches(sender.Text.Trim()).FirstOrDefault();
            if (hit is null) return;
            sender.Text = "";
            sender.ItemsSource = null;
            Reveal(hit);
        };
        Nav.AutoSuggestBox = box;
    }

    /// <summary>Settings showing now whose names contain <paramref name="query"/>: names that start with it first.</summary>
    private IEnumerable<SearchHit> Matches(string query)
    {
        if (query.Length == 0) return [];
        return SearchIndex()
            .Where(h => h.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) && h.Path.All(e => e.Visibility == Visibility.Visible))
            .OrderBy(h => h.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1);
    }

    private List<SearchHit> SearchIndex()
    {
        if (_searchIndex is not null) return _searchIndex;
        var index = new List<SearchHit>();
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag is not string page) continue;
            Walk(PageFor(page), page, [], [], index);
        }
        return _searchIndex = index;
    }

    /// <summary>Every card, expander and section heading in a built page, with what contains it.</summary>
    private static void Walk(UIElement element, string page, List<UIElement> path, List<Expander> expanders, List<SearchHit> index)
    {
        var here = new List<UIElement>(path) { element };
        switch (element)
        {
            case Card card:
                if (card.Title.Length > 0) index.Add(new SearchHit(card.Title, page, card, here, expanders));
                break;
            case Expander expander:
                if (expander.Header is Card header && header.Title.Length > 0)
                    index.Add(new SearchHit(header.Title, page, expander, here, expanders));
                if (expander.Content is UIElement content) Walk(content, page, here, [.. expanders, expander], index);
                break;
            case TextBlock heading when AutomationProperties.GetHeadingLevel(heading) == AutomationHeadingLevel.Level2:
                index.Add(new SearchHit(heading.Text, page, heading, here, expanders));
                break;
            case Panel panel:
                foreach (var child in panel.Children) Walk(child, page, here, expanders, index);
                break;
            case Border border when border.Child is not null:
                Walk(border.Child, page, here, expanders, index);
                break;
        }
    }

    private void Reveal(SearchHit hit)
    {
        ShowPage(hit.Page);
        foreach (var expander in hit.Expanders) expander.IsExpanded = true;
        // After the page is in the window and the expanders have opened.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            hit.Target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.2 });
            if (hit.Target is Control control) control.Focus(FocusState.Programmatic);
            else if (FirstControl(hit.Target) is { } inner) inner.Focus(FocusState.Programmatic);
        });
    }

    private static Control? FirstControl(DependencyObject root)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Control { IsTabStop: true, IsEnabled: true } control) return control;
            if (FirstControl(child) is { } found) return found;
        }
        return null;
    }
}
