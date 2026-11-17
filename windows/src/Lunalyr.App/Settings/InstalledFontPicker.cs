// Typeface › Installed font (the Mac's InstalledFontPicker, 7fa2f68 and 86af9d7; Lunalyr's own lines): the chosen family in
// its own face, which opens a searchable list of the families on this PC (each drawn in itself, as the list scrolls to
// it, so hundreds stay quick), and "Find new fonts". A family uninstalled since says so, while the lyrics use the default
// typeface (AppSettings.InstalledFontMissing).

using Lunalyr.Shell.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

internal static class InstalledFontPicker
{
    public const double ListHeight = 196;

    public const string Summary = "Fonts installed on this PC.";

    public const string Detail =
        "Every font family installed on this PC that can set lyrics. Install a font (right-click it and choose Install), then Find new fonts.";

    /// <summary>What shows while the saved family is missing (the Mac's warning, with Windows' default typeface).</summary>
    public static string MissingMessage(string family) =>
        $"“{family}” isn’t installed any more, so the lyrics use Segoe UI Variable, the default. Reinstall it or pick another.";

    /// <summary>The list's line when nothing shows in it.</summary>
    public static string EmptyMessage(bool loaded, string query) =>
        !loaded ? "Reading fonts…" : query.Trim().Length == 0 ? "No fonts found." : $"No fonts match “{query.Trim()}”.";

    public static StackPanel Build(SettingsContext ctx, InstalledFontLibrary library)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left };
        var open = false;
        var query = "";

        // The family in use, in its own face, and the switch for the list.
        var chosenText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var chevron = new FontIcon { Glyph = "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        var chosenRow = new Grid { ColumnSpacing = 8 };
        chosenRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        chosenRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chosenRow.Children.Add(chosenText);
        Grid.SetColumn(chevron, 1);
        chosenRow.Children.Add(chevron);
        var chosen = new Button
        {
            Content = chosenRow, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 32,
        };
        AutomationProperties.SetName(chosen, "Installed family");
        panel.Children.Add(chosen);

        var missing = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false };
        panel.Children.Add(missing);

        var search = new TextBox();
        AutomationProperties.SetName(search, "Search installed fonts");
        var list = new ListView
        {
            Height = ListHeight, SelectionMode = ListViewSelectionMode.Single, BorderThickness = new Thickness(1),
            BorderBrush = Resource<Brush>("CardStrokeColorDefaultBrush"), CornerRadius = new CornerRadius(6),
            ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">"
                + "<TextBlock Text=\"{Binding}\" TextTrimming=\"CharacterEllipsis\"/></DataTemplate>"),
        };
        AutomationProperties.SetName(list, "Installed fonts");
        var empty = new TextBlock
        {
            Style = Resource<Style>("SettingsCaption"), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, IsHitTestVisible = false,
        };
        var listArea = new Grid();
        listArea.Children.Add(list);
        listArea.Children.Add(empty);
        var picker = new StackPanel { Spacing = 6 };
        picker.Children.Add(search);
        picker.Children.Add(listArea);
        panel.Children.Add(picker);

        // Each row in its own family, set as the row comes into view (so hundreds stay quick).
        list.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue || args.Item is not string family) return;
            if (args.ItemContainer.ContentTemplateRoot is TextBlock text) text.FontFamily = new FontFamily(family);
            ToolTipService.SetToolTip(args.ItemContainer, family);
            AutomationProperties.SetName(args.ItemContainer, family);
        };

        var find = new Button { Content = "Find new fonts" };
        ToolTipService.SetToolTip(find, "Look again for fonts installed since Lunalyr opened");
        var found = new TextBlock { Style = Resource<Style>("SettingsCaption"), VerticalAlignment = VerticalAlignment.Center };
        var findRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        findRow.Children.Add(find);
        findRow.Children.Add(found);
        panel.Children.Add(findRow);
        panel.Children.Add(Caption($"{Summary} {Detail}"));

        var filling = false;
        void ShowList(AppSettings s)
        {
            var families = InstalledFontLibrary.Filter(library.Families, query);
            filling = true;
            list.ItemsSource = families.ToList(); // a List: WinUI binds IList, not a read-only list
            list.SelectedItem = s.InstalledFontMissing ? null : families.FirstOrDefault(f => string.Equals(f, s.FontFamily, StringComparison.OrdinalIgnoreCase));
            filling = false;
            if (list.SelectedItem is { } selected) list.ScrollIntoView(selected, ScrollIntoViewAlignment.Default);
            empty.Text = families.Count == 0 ? EmptyMessage(library.HasLoaded, query) : "";
            empty.Visibility = families.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            search.PlaceholderText = $"Search {library.Families.Count} fonts";
        }

        void Refresh(AppSettings s)
        {
            if (s.FontChoice != "installed") return;
            library.LoadIfNeeded();
            var family = s.FontFamily.Trim();
            var lost = s.InstalledFontMissing;
            chosenText.Text = family.Length == 0 ? "Choose a family" : family;
            chosenText.FontFamily = family.Length == 0 || lost ? FontFamily.XamlAutoFontFamily : new FontFamily(family);
            chosenText.Opacity = family.Length == 0 ? 0.7 : 1;
            AutomationProperties.SetHelpText(chosen, open ? "Hides the list of installed fonts" : "Shows the list of installed fonts");
            missing.Message = lost ? MissingMessage(family) : "";
            missing.IsOpen = lost;
            // Nothing picked yet: the list is what there is to do.
            if (family.Length == 0) open = true;
            chevron.Glyph = open ? "" : "";
            picker.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open) ShowList(s);
        }
        ctx.OnRefresh(Refresh);

        chosen.Click += (_, _) =>
        {
            open = !open;
            Refresh(ctx.Current);
            if (open) search.Focus(FocusState.Programmatic);
        };
        search.TextChanged += (_, _) =>
        {
            query = search.Text;
            ShowList(ctx.Current);
        };
        list.SelectionChanged += (_, _) =>
        {
            if (filling || list.SelectedItem is not string family) return;
            ctx.Update(s => s with { FontChoice = "installed", FontFamily = family });
        };

        // The answer stays a few seconds, then goes.
        var clear = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        if (clear is not null)
        {
            clear.Interval = TimeSpan.FromSeconds(4);
            clear.IsRepeating = false;
            clear.Tick += (_, _) => found.Text = "";
        }
        find.Click += (_, _) =>
        {
            find.IsEnabled = false;
            try
            {
                var added = library.FindNewFonts();
                found.Text = InstalledFontLibrary.FoundMessage(added);
            }
            finally
            {
                find.IsEnabled = true;
            }
            Refresh(ctx.Current);
            clear?.Stop();
            clear?.Start();
        };
        return panel;
    }
}
