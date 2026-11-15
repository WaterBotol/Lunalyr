using Lunalyr.Core.Compositions;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Lunalyr.App.Settings;

/// <summary>
/// The Word symbol list and Action word list (the Mac's ad5a6a6 and cba108f): one row per word with its value and a
/// remove button, the default words first and then the user's own A to Z, an "Add a word" field (the word stored folded,
/// with <c>star</c> or <c>spin</c>) and Restore Defaults (the stored list removed). Any edit stores the whole list.
/// </summary>
internal static class WordListEditor
{
    /// <summary>One value a word can take: its stored name, what the picker shows, and an optional icon glyph.</summary>
    public readonly record struct Choice(string Value, string Label, string? Glyph = null);

    public static readonly IReadOnlyList<Choice> SymbolChoices =
    [
        new("heart.fill", "Heart", ""), new("heart", "Heart outline", ""), new("heart.slash", "Broken heart", ""),
        new("flame", "Flame", ""), new("star", "Star", ""), new("moon", "Moon", ""),
        new("sun.max", "Sun", ""), new("cloud.rain", "Rain", ""), new("moon.stars", "Night", ""),
        new("figure.dance", "Dance", ""), new("drop", "Drop", ""), new("dollarsign", "Dollar", null),
        new("crown", "Crown", ""), new("person", "Person", ""), new("house", "Home", ""),
        new("clock", "Clock", ""), new("music.note", "Music note", ""), new("cloud", "Cloud", ""),
        new("globe", "Globe", ""), new("phone", "Phone", ""),
    ];

    public static readonly IReadOnlyList<Choice> ActionChoices =
    [
        new("spin", "Spin"), new("float", "Float"), new("dip", "Dip"), new("shake", "Shake"), new("hop", "Hop"),
        new("grow", "Grow"), new("shrink", "Shrink"), new("dissolve", "Dissolve"), new("flicker", "Flicker"), new("frost", "Frost"),
    ];

    public static StackPanel Build(SettingsContext ctx, string itemName,
                                   Func<AppSettings, IReadOnlyDictionary<string, string>?> get,
                                   Func<AppSettings, IReadOnlyDictionary<string, string>?, AppSettings> set,
                                   IReadOnlyList<KeyValuePair<string, string>> defaults, IReadOnlyList<Choice> choices, string newValue)
    {
        var panel = new StackPanel { Spacing = 6 };
        var rows = new StackPanel { Spacing = 4 };
        var note = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var addBox = new TextBox { PlaceholderText = "Add a word", MinWidth = 180 };
        AutomationProperties.SetName(addBox, $"Add a word to the {itemName}");
        var add = new Button { Content = "Add" };
        var restore = new Button { Content = "Restore Defaults" };
        AutomationProperties.SetName(restore, $"Restore the default {itemName}");
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        footer.Children.Add(addBox);
        footer.Children.Add(add);
        footer.Children.Add(restore);
        panel.Children.Add(rows);
        panel.Children.Add(footer);
        panel.Children.Add(note);

        Dictionary<string, string> Effective(AppSettings s) =>
            get(s) is { } stored ? new Dictionary<string, string>(stored, StringComparer.Ordinal)
                : defaults.GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);

        void Store(Func<Dictionary<string, string>, bool> change)
        {
            var list = Effective(ctx.Current);
            if (change(list)) ctx.Update(s => set(s, list));
        }

        void AddWord()
        {
            var typed = addBox.Text;
            if (WordAccents.EnglishKey(typed) is not { } key)
            {
                note.Text = string.IsNullOrWhiteSpace(typed) ? "" : "Only English words (letters a–z and digits) can be listed.";
                note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            note.Visibility = Visibility.Collapsed;
            addBox.Text = "";
            Store(list => list.TryAdd(key, newValue));
        }
        add.Click += (_, _) => AddWord();
        addBox.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            AddWord();
        };
        restore.Click += (_, _) => ctx.Update(s => set(s, null));

        IReadOnlyDictionary<string, string>? shown = null;
        var built = false;
        ctx.OnRefresh(s =>
        {
            var stored = get(s);
            if (built && ReferenceEquals(stored, shown)) return;
            built = true;
            shown = stored;
            restore.IsEnabled = stored is not null;
            rows.Children.Clear();
            foreach (var (word, value) in WordAccents.Ordered(Effective(s), defaults))
                rows.Children.Add(Row(word, value));
        });
        return panel;

        FrameworkElement Row(string word, string value)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = word, VerticalAlignment = VerticalAlignment.Center };
            var picker = new ComboBox { MinWidth = 170 };
            AutomationProperties.SetName(picker, $"{word}: {itemName} value");
            var values = new List<string>();
            foreach (var choice in choices)
            {
                picker.Items.Add(Item(choice));
                values.Add(choice.Value);
            }
            if (!values.Contains(value))
            {
                // A name Windows can't draw (set on the Mac, say): kept, shown as it is.
                picker.Items.Add(new ComboBoxItem { Content = $"{value} (not shown on Windows)" });
                values.Add(value);
            }
            picker.SelectedIndex = values.IndexOf(value);
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex < 0 || values[picker.SelectedIndex] == value) return;
                var chosen = values[picker.SelectedIndex];
                Store(list => { list[word] = chosen; return true; });
            };
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(8, 6, 8, 6) };
            AutomationProperties.SetName(remove, $"Remove {word}");
            ToolTipService.SetToolTip(remove, $"Remove {word}");
            remove.Click += (_, _) => Store(list => list.Remove(word));
            Grid.SetColumn(picker, 1);
            Grid.SetColumn(remove, 2);
            grid.Children.Add(label);
            grid.Children.Add(picker);
            grid.Children.Add(remove);
            return grid;
        }
    }

    private static ComboBoxItem Item(Choice choice)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (choice.Glyph is { } glyph) content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        else if (choice.Value == "dollarsign") content.Children.Add(new TextBlock { Text = "$", Width = 14, TextAlignment = TextAlignment.Center });
        content.Children.Add(new TextBlock { Text = choice.Label });
        var item = new ComboBoxItem { Content = content };
        AutomationProperties.SetName(item, choice.Label);
        return item;
    }
}
