using Lunalyr.Platform.Settings;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Lunalyr.App.Settings;

/// <summary>
/// What every settings page shares: the settings store, a refresh list that puts the current settings into the
/// controls (on the UI thread, whatever thread changed them), and a guard so that putting values into controls never
/// writes them back. Pages are built in code from <see cref="SettingsKit"/>'s rows: no runtime data binding, which
/// Native AOT would trim.
/// </summary>
internal sealed class SettingsContext
{
    private readonly List<Action<AppSettings>> _refreshers = [];
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<string>? _log;
    private string? _lastLog;
    private long _lastLogAt;
    private int _queued;

    public SettingsContext(SettingsStore<AppSettings> store, DispatcherQueue dispatcher, Action<string>? log = null)
    {
        Store = store;
        _dispatcher = dispatcher;
        _log = log;
        store.Changed += OnChanged;
    }

    public SettingsStore<AppSettings> Store { get; }

    /// <summary>The window closed: stop following the store.</summary>
    public void Detach()
    {
        Store.Changed -= OnChanged;
        _refreshers.Clear();
        Detached?.Invoke();
        Detached = null;
    }

    /// <summary>Raised once when the window closes, for pages that follow something else (a sign-in, an account).</summary>
    public event Action? Detached;

    private void OnChanged(object? sender, SettingsChangedEventArgs<AppSettings> e) => QueueRefresh();

    public AppSettings Current => Store.Current;

    /// <summary>True while values are being put into the controls.</summary>
    public bool Loading { get; private set; }

    /// <summary>Changes the settings, unless the change comes from putting values into the controls.</summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        if (Loading) return;
        var before = Store.Current;
        if (!Store.Update(change) || _log is null) return;
        var keys = AppSettings.ChangedKeys(before, Store.Current);
        if (keys.Count == 0) return;
        // A slider dragged writes many times a second: one line per run of the same keys.
        var message = $"Settings changed in the window: {string.Join(", ", keys)}.";
        var now = Environment.TickCount64;
        if (message == _lastLog && now - _lastLogAt < 2000) return;
        _lastLog = message;
        _lastLogAt = now;
        _log(message);
    }

    /// <summary>Runs <paramref name="refresh"/> now and whenever the settings change.</summary>
    public void OnRefresh(Action<AppSettings> refresh)
    {
        _refreshers.Add(refresh);
        Run(refresh, Current);
    }

    /// <summary>Puts the current settings into every control.</summary>
    public void Refresh()
    {
        var settings = Current;
        foreach (var refresh in _refreshers.ToArray()) Run(refresh, settings);
    }

    /// <summary>Other things a page shows changed (the song, the controller's status): refresh on the UI thread.</summary>
    public void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        _dispatcher.TryEnqueue(() =>
        {
            Volatile.Write(ref _queued, 0);
            Refresh();
        });
    }

    private void Run(Action<AppSettings> refresh, AppSettings settings)
    {
        var was = Loading;
        Loading = true;
        try
        {
            refresh(settings);
        }
        finally
        {
            Loading = was;
        }
    }
}

/// <summary>
/// Windows 11 settings rows built in code: section headers, cards with an icon, a title, a description and a control,
/// expanders for lists, and disabled rows that say which milestone brings them. Every control gets its row's title as
/// its Narrator name and the description as its help text. Colours come only from theme resources, so light, dark,
/// the accent colour and high contrast all apply.
/// </summary>
internal static class SettingsKit
{
    public static T Resource<T>(string key) => (T)Application.Current.Resources[key];

    public static StackPanel Page() => new() { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };

    /// <summary>A section's heading (a level-2 heading for Narrator).</summary>
    public static TextBlock Section(StackPanel page, string title)
    {
        var heading = new TextBlock
        {
            Text = title,
            Style = Resource<Style>("BodyStrongTextBlockStyle"),
            Margin = new Thickness(1, page.Children.Count == 0 ? 0 : 22, 0, 6),
        };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        page.Children.Add(heading);
        return heading;
    }

    /// <summary>A caption under a section or row, in the secondary text colour.</summary>
    public static TextBlock Caption(string text, bool selectable = false) => new()
    {
        Text = text,
        Style = Resource<Style>("SettingsCaption"),
        IsTextSelectionEnabled = selectable,
    };

    /// <summary>A Segoe Fluent Icons glyph.</summary>
    public static FontIcon Icon(string glyph, double size = 18) => new()
    {
        Glyph = glyph,
        FontSize = size,
    };

    /// <summary>
    /// A settings card: icon, title, description (and a note when the row can't be used yet), with
    /// <paramref name="control"/> on the right, or below when <paramref name="stacked"/>.
    /// </summary>
    public static Card Card(string title, string? description = null, string? glyph = null, FrameworkElement? control = null,
                            bool stacked = false, string? notYet = null)
    {
        var card = new Card(title, description, glyph, control, stacked, notYet);
        // Narrator names the row's control after the row (a slider row names its slider), with the description and
        // note as its help text.
        if (Named(control) is { } named)
        {
            if (string.IsNullOrEmpty(AutomationProperties.GetName(named))) AutomationProperties.SetName(named, title);
            if (string.IsNullOrEmpty(AutomationProperties.GetHelpText(named)))
            {
                card.HelpTarget = named;
                card.Note = notYet;
                card.Description = description ?? "";
            }
        }
        return card;
    }

    /// <summary>The control a row's title names: the control itself, or the slider in a slider row.</summary>
    private static FrameworkElement? Named(FrameworkElement? control) => control switch
    {
        null => null,
        Control c => c,
        Panel panel => panel.Children.OfType<Slider>().FirstOrDefault() ?? (panel.Children.OfType<Panel>().Select(Named).FirstOrDefault(n => n is not null)),
        _ => control,
    };

    public static ToggleSwitch Toggle(SettingsContext ctx, Func<AppSettings, bool> get, Func<AppSettings, bool, AppSettings> set)
    {
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0 };
        ctx.OnRefresh(s => toggle.IsOn = get(s));
        toggle.Toggled += (_, _) => ctx.Update(s => set(s, toggle.IsOn));
        return toggle;
    }

    /// <summary>A disabled switch showing <paramref name="on"/>: for rows Windows can't change yet.</summary>
    public static ToggleSwitch FixedToggle(bool on) => new() { OnContent = "On", OffContent = "Off", MinWidth = 0, IsOn = on, IsEnabled = false };

    public static ComboBox Combo(SettingsContext ctx, IReadOnlyList<SettingsChoice> choices, Func<AppSettings, string> get,
                                 Func<AppSettings, string, AppSettings> set, double width = 260)
    {
        var combo = new ComboBox { MinWidth = width, MaxWidth = 360 };
        foreach (var choice in choices) combo.Items.Add(new ComboBoxItem { Content = choice.Title, Tag = choice.Value });
        ctx.OnRefresh(s =>
        {
            var value = get(s);
            var index = -1;
            for (var i = 0; i < choices.Count; i++) if (choices[i].Value == value) index = i;
            combo.SelectedIndex = index;
        });
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string value }) ctx.Update(s => set(s, value));
        };
        return combo;
    }

    /// <summary>A disabled menu showing one value.</summary>
    public static ComboBox FixedCombo(string shown)
    {
        var combo = new ComboBox { MinWidth = 200, IsEnabled = false };
        combo.Items.Add(new ComboBoxItem { Content = shown });
        combo.SelectedIndex = 0;
        return combo;
    }

    /// <summary>
    /// A slider with its value written beside it (and spoken by Narrator in the same words), optionally with words at
    /// either end ("Later", "Earlier").
    /// </summary>
    public static FrameworkElement Slider(SettingsContext ctx, double min, double max, double step, Func<double, string> format,
                                          Func<AppSettings, double> get, Func<AppSettings, double, AppSettings> set,
                                          string? minimumLabel = null, string? maximumLabel = null, Func<double, string>? spoken = null,
                                          double width = 220)
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, StepFrequency = step, SmallChange = step, LargeChange = step * 5,
            Width = width, IsThumbToolTipEnabled = false, VerticalAlignment = VerticalAlignment.Center,
        };
        return SliderRow(slider, format, spoken, minimumLabel, maximumLabel, () =>
        {
            ctx.OnRefresh(s => slider.Value = get(s));
            slider.ValueChanged += (_, e) => ctx.Update(s => set(s, e.NewValue));
        });
    }

    /// <summary>A slider row around a slider whose value the caller manages.</summary>
    public static FrameworkElement SliderRow(Slider slider, Func<double, string> format, Func<double, string>? spoken,
                                             string? minimumLabel, string? maximumLabel, Action wire)
    {
        var value = new TextBlock
        {
            Style = Resource<Style>("SettingsValue"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 64,
            TextAlignment = TextAlignment.Right,
        };
        AutomationProperties.SetAccessibilityView(value, AccessibilityView.Raw);
        void Show(double v)
        {
            value.Text = format(v);
            AutomationProperties.SetItemStatus(slider, (spoken ?? format)(v));
        }
        slider.ValueChanged += (_, e) => Show(e.NewValue);
        wire();
        Show(slider.Value);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(value);
        if (minimumLabel is not null) row.Children.Add(EndLabel(minimumLabel));
        row.Children.Add(slider);
        if (maximumLabel is not null) row.Children.Add(EndLabel(maximumLabel));
        return row;

        static TextBlock EndLabel(string text)
        {
            var label = new TextBlock
            {
                Text = text,
                Style = Resource<Style>("SettingsCaption"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetAccessibilityView(label, AccessibilityView.Raw);
            return label;
        }
    }

    /// <summary>A disabled slider at <paramref name="value"/>.</summary>
    public static Slider FixedSlider(double min, double max, double value) =>
        new() { Minimum = min, Maximum = max, Value = value, Width = 220, IsEnabled = false, IsThumbToolTipEnabled = false };

    public static Button Button(string text, Action click, string? name = null)
    {
        var button = new Button { Content = text };
        if (name is not null) AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>An expander card: a header like a card's, with <paramref name="content"/> inside.</summary>
    public static Expander Expander(string title, string? description, string? glyph, UIElement content, bool expanded = false,
                                    string? notYet = null)
    {
        var header = new Card(title, description, glyph, null, false, notYet, bare: true);
        var expander = new Expander
        {
            Header = header,
            Content = content,
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(expander, title);
        if (description is not null) AutomationProperties.SetHelpText(expander, description);
        return expander;
    }

    /// <summary>Shows <paramref name="element"/> only while <paramref name="visible"/> holds.</summary>
    public static T ShowWhen<T>(this T element, SettingsContext ctx, Func<AppSettings, bool> visible) where T : UIElement
    {
        ctx.OnRefresh(s => element.Visibility = visible(s) ? Visibility.Visible : Visibility.Collapsed);
        return element;
    }

    /// <summary>Enables <paramref name="control"/> only while <paramref name="enabled"/> holds.</summary>
    public static T EnableWhen<T>(this T control, SettingsContext ctx, Func<AppSettings, bool> enabled) where T : Control
    {
        ctx.OnRefresh(s => control.IsEnabled = enabled(s));
        return control;
    }
}

/// <summary>One settings card (Windows 11 style): icon, title, description, note, control.</summary>
internal sealed partial class Card : UserControl
{
    private readonly TextBlock _title;
    private readonly TextBlock _description;

    public Card(string title, string? description, string? glyph, FrameworkElement? control, bool stacked, string? notYet, bool bare = false)
    {
        IsTabStop = false;
        var border = new Border();
        if (!bare)
        {
            border.Style = SettingsKit.Resource<Style>("Card");
            border.Padding = new Thickness(16, 12, 16, 12);
            border.MinHeight = 64;
        }
        else
        {
            border.Padding = new Thickness(0, 8, 0, 8);
        }

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var disabled = notYet is not null;
        if (glyph is not null)
        {
            var icon = SettingsKit.Icon(glyph, 20);
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Style = SettingsKit.Resource<Style>(disabled ? "SettingsIconDisabled" : "SettingsIcon");
            AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
            grid.Children.Add(icon);
        }
        else
        {
            // No icon: keep its column, so the title lines up with the rows that have one (an expander header has none).
            if (bare) grid.ColumnSpacing = 0;
            else grid.Children.Add(new Border { Width = 20 });
        }

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Margin = new Thickness(glyph is null ? 0 : 0, 0, 12, 0) };
        _title = new TextBlock
        {
            Text = title,
            Style = SettingsKit.Resource<Style>(disabled ? "SettingsTitleDisabled" : "SettingsTitle"),
            TextWrapping = TextWrapping.Wrap,
        };
        texts.Children.Add(_title);
        _description = SettingsKit.Caption(description ?? "");
        _description.Visibility = string.IsNullOrEmpty(description) ? Visibility.Collapsed : Visibility.Visible;
        texts.Children.Add(_description);
        if (notYet is not null)
        {
            var note = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
            var info = SettingsKit.Icon("", 12);
            info.Style = SettingsKit.Resource<Style>("SettingsNoteIcon");
            info.VerticalAlignment = VerticalAlignment.Center;
            note.Children.Add(info);
            note.Children.Add(SettingsKit.Caption(notYet));
            texts.Children.Add(note);
        }
        Grid.SetColumn(texts, 1);
        // Title and description are read with the control (its name and help text), not again on their own.
        if (control is not null) AutomationProperties.SetAccessibilityView(texts, AccessibilityView.Raw);
        grid.Children.Add(texts);

        if (control is not null)
        {
            if (disabled && control is Control c) c.IsEnabled = false;
            if (stacked)
            {
                Grid.SetRow(control, 1);
                Grid.SetColumn(control, 1);
                Grid.SetColumnSpan(control, 2);
                control.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
            else
            {
                Grid.SetColumn(control, 2);
                control.VerticalAlignment = VerticalAlignment.Center;
                control.HorizontalAlignment = HorizontalAlignment.Right;
            }
            grid.Children.Add(control);
        }

        border.Child = grid;
        Content = border;
    }

    /// <summary>The card's description, for rows whose caption follows the choice.</summary>
    public string Description
    {
        get => _description.Text;
        set
        {
            _description.Text = value;
            _description.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            if (HelpTarget is not null)
                AutomationProperties.SetHelpText(HelpTarget, string.Join(" ", new[] { value, Note }.Where(s => !string.IsNullOrEmpty(s))));
        }
    }

    /// <summary>The control whose help text follows the description.</summary>
    public FrameworkElement? HelpTarget { get; set; }

    /// <summary>The row's "arrives in" note, read after the description.</summary>
    public string? Note { get; set; }

    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }
}
