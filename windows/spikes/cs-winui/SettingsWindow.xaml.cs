using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LunalyrSpike;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        // Card style, defined in code so the spike stays a single XAML file per window.
        var card = new Style(typeof(Border));
        card.Setters.Add(new Setter(Border.BackgroundProperty, Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]));
        card.Setters.Add(new Setter(Border.BorderBrushProperty, Application.Current.Resources["CardStrokeColorDefaultBrush"]));
        card.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
        card.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(8)));
        card.Setters.Add(new Setter(Border.PaddingProperty, new Thickness(16, 12, 16, 12)));
        card.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 68.0));
        Application.Current.Resources["Card"] = card;

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && PageTitle != null) PageTitle.Text = item.Tag as string ?? "";
    }
}
