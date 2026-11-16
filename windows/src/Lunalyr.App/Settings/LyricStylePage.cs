using Lunalyr.Core.Motion.ThinkingOrbs;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Shell;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Lunalyr.App.Settings.SettingsGroups;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>
/// Lyric style (the Mac's LyricStylePage): where lyrics show, the layout and Custom Mix's rotation, the opening
/// title, credits and backing vocals, Editorial typography, size, typeface and mixed fonts, text and highlight colours,
/// word entrances, the reveal and the next-word preview, the constellations' and Echo's own sliders, instrumental breaks
/// and remembered styles.
/// </summary>
internal static class LyricStylePage
{
    private const string Automatic = AppSettings.AllLayoutsKey;

    public static StackPanel Build(SettingsContext ctx, Func<IReadOnlyList<string>> installedFonts, Func<bool> reduceMotion,
                                   UIElement? preview, FloatingHooks floating, Func<AppController?> controller)
    {
        // The song playing, for remembered styles: its query (once its length is known) and artist.
        PlayingSong Playing() => new(controller()?.CurrentQuery, controller()?.Status.Artist);

        var page = Page();
        if (preview is not null) page.Children.Add(preview);

        // ---- Show lyrics: on the desktop or floating, and which one's style the rest edits ----
        FloatingLyricsSection.AddSurface(page, ctx);
        FloatingLyricsSection.AddFloating(page, ctx, floating);

        // ---- Layout ----
        Section(page, "Layout");
        var layoutCard = Card("Layout", null, "",
            Combo(ctx, SettingsCopy.Layouts, s => s.LyricComposition, (s, v) => s with { LyricComposition = v }));
        // Under Custom Mix, which layout the song playing took (the Mac's 68bac63: "Custom Mix · Hinge"), before what it does.
        void ShowLayout(AppSettings s)
        {
            var detail = SettingsCopy.Layouts.FirstOrDefault(c => c.Value == s.LyricComposition)?.Detail ?? "";
            var showing = s.LyricComposition == Automatic && controller()?.Status.Title is not null ? controller()?.MatchedComposition : null;
            layoutCard.Description = showing is null ? detail : $"Now {SettingsCopy.ShowingLayoutTitle(Automatic, showing)}. {detail}";
        }
        ctx.OnRefresh(ShowLayout);
        if (controller() is { } playingController)
        {
            void Changed(object? sender, EventArgs e) => layoutCard.DispatcherQueue?.TryEnqueue(() => ShowLayout(ctx.Current));
            playingController.StatusChanged += Changed;
            ctx.Detached += () => playingController.StatusChanged -= Changed;
        }
        page.Children.Add(layoutCard);
        page.Children.Add(RememberedNote(ctx, Playing, StyleAspect.Layout));
        page.Children.Add(Card("Change layout every song", "Custom Mix takes the next layout for each new song.",
                "", Toggle(ctx, s => s.RotateLayouts, (s, v) => s with { RotateLayouts = v }))
            .ShowWhen(ctx, s => s.LyricComposition == Automatic));
        page.Children.Add(Card("Show the song title at the start", "Title, artist and album as a song begins.",
            "", Toggle(ctx, s => s.OpeningTitle, (s, v) => s with { OpeningTitle = v })));
        // The Mac's d849c49 and e30fbb2: kept for the whole song, and how strong it stays once it rests.
        page.Children.Add(Indented(Card("Keep the song title on screen", LyricStyleCopy.KeepTitle, null,
            Toggle(ctx, s => s.OpeningTitleAlways, (s, v) => s with { OpeningTitleAlways = v }).EnableWhen(ctx, s => s.OpeningTitle))));
        page.Children.Add(Indented(Card("Title strength", LyricStyleCopy.TitleStrength, null,
                Slider(ctx, Core.Compositions.OpeningTitle.StrengthMin, Core.Compositions.OpeningTitle.StrengthMax, 0.01, SettingsCopy.Percent,
                    s => s.OpeningTitleStrength, (s, v) => s with { OpeningTitleStrength = Math.Round(v, 2) }))
            .ShowWhen(ctx, s => s.OpeningTitle && s.OpeningTitleAlways)));
        // The Mac's 3276613 and dece022: the title is dragged on the picture above; this puts it back, on the surface shown.
        var resetTitle = Button("Reset Title Position", () => ctx.Update(s => s.IsFloating
            ? s.WithTitleRow(Core.Compositions.OpeningTitleRow.Standard)
            : s.WithTitlePlacement(Core.Compositions.OpeningTitlePlacement.Standard)));
        var resetTitleCard = Indented(Card("Song title position", null, null, resetTitle));
        ctx.OnRefresh(s =>
        {
            var help = s.IsFloating ? LyricStyleCopy.ResetTitleFloating : LyricStyleCopy.ResetTitleDesktop;
            resetTitleCard.Description = help;
            AutomationProperties.SetHelpText(resetTitle, help);
            resetTitle.IsEnabled = s.OpeningTitle && (s.IsFloating ? !s.TitleRow().IsStandard : !s.TitlePlacement().IsStandard);
        });
        page.Children.Add(resetTitleCard);

        // Advanced layout
        page.Children.Add(Advanced(Expander("Layouts in the rotation",
                "How many songs in a row each layout gets. 0 leaves a layout out.", "", RotationList(ctx))
            .ShowWhen(ctx, s => s.LyricComposition == Automatic && s.RotateLayouts)));
        page.Children.Add(Advanced(Card("Album cover with the title", "The cover beside the song’s title at the start.", "",
            Toggle(ctx, s => s.OpeningTitleCover, (s, v) => s with { OpeningTitleCover = v }).EnableWhen(ctx, s => s.OpeningTitle))));
        page.Children.Add(Advanced(Card("Credits in the background", "Lines like “Produced by: …” behind the lyrics, one at a time.",
            "", Toggle(ctx, s => s.CreditsLayer, (s, v) => s with { CreditsLayer = v }))));
        page.Children.Add(Advanced(Card("Backing vocals in the background", LyricStyleCopy.BackingVocals, "",
            Toggle(ctx, s => s.BackingVocalsLayer, (s, v) => s with { BackingVocalsLayer = v }))));
        page.Children.Add(Advanced(Indented(Card("Backing vocals move like", null, null,
                Combo(ctx, LyricStyleCopy.BackingChoices, s => s.BackingVocalStyle, (s, v) => s with { BackingVocalStyle = v })))
            .ShowWhen(ctx, s => s.BackingVocalsLayer)));
        // The layouts' own sliders, only while the layout can use them.
        page.Children.Add(Advanced(Card("Constellation spread", null, "",
                Slider(ctx, AppSettings.SpreadMin, AppSettings.SpreadMax, 0.01, SettingsCopy.Percent, s => s.ConstellationSpread,
                    (s, v) => s with { ConstellationSpread = Math.Round(v, 2) }))
            .ShowWhen(ctx, s => s.LyricComposition is Automatic or "constellation")));
        page.Children.Add(Advanced(Card("Constellation links", null, "",
                Slider(ctx, AppSettings.LinksMin, AppSettings.LinksMax, 0.01, SettingsCopy.Percent, s => s.ConstellationLinkOpacity,
                    (s, v) => s with { ConstellationLinkOpacity = Math.Round(v, 2) }))
            .ShowWhen(ctx, s => s.LyricComposition is Automatic or "constellation")));
        page.Children.Add(Advanced(Card("Echo repeats", null, "",
                Slider(ctx, AppSettings.RepeatsMin, AppSettings.RepeatsMax, 0.01, SettingsCopy.Percent, s => s.EchoStrength,
                    (s, v) => s with { EchoStrength = Math.Round(v, 2) }))
            .ShowWhen(ctx, s => s.LyricComposition is Automatic or "echo")));
        // Remembered styles: a layout and colour kept for a song or an artist.
        var memoryStart = page.Children.Count;
        page.Children.Add(Card("Use remembered styles", "A layout and colour kept for a song or artist wins while it plays.", "",
            Toggle(ctx, s => s.StyleMemoryEnabled, (s, v) => s with { StyleMemoryEnabled = v })));
        AddMemory(page, ctx, Playing);
        AdvancedFrom(page, memoryStart);

        // ---- Text ----
        Section(page, "Text");
        var typeface = Card("Typeface", null, "",
            Combo(ctx, SettingsCopy.FontChoices, s => s.FontChoice, (s, v) => s with { FontChoice = v }));
        ctx.OnRefresh(s =>
        {
            var detail = SettingsCopy.FontChoices.FirstOrDefault(c => c.Value == s.FontChoice)?.Detail ?? "";
            typeface.Description = s.RandomizeLyricFonts ? $"{detail} {LyricStyleCopy.RandomisedTypeface}".Trim() : detail;
        });
        page.Children.Add(typeface);
        page.Children.Add(Indented(Card("Font family", "Type to jump to a family.", null, FontList(ctx, installedFonts)))
            .ShowWhen(ctx, s => s.FontChoice == "installed"));
        // Floating has its own size (Floating lyrics, above).
        page.Children.Add(Card("Lyric size", null, "",
            Slider(ctx, 0.7, 1.5, 0.01, SettingsCopy.Percent, s => s.LyricScale, (s, v) => s with { LyricScale = Math.Round(v, 2) }))
            .ShowWhen(ctx, s => !s.IsFloating));

        // Advanced text
        page.Children.Add(Advanced(Card("Randomise lyric fonts", LyricStyleCopy.Randomise, "",
            Toggle(ctx, s => s.RandomizeLyricFonts, (s, v) => s with { RandomizeLyricFonts = v }))));
        var editorial = Card("Mix in a second typeface", LyricStyleCopy.EditorialTypography, "",
            Toggle(ctx, s => s.EditorialTypography, (s, v) => s with { EditorialTypography = v }));
        ctx.OnRefresh(s => editorial.Description = LyricStyleCopy.EditorialNote(SettingsCopy.Kind(s.LyricComposition)) is { } note
            ? $"{LyricStyleCopy.EditorialTypography} {note}" : LyricStyleCopy.EditorialTypography);
        page.Children.Add(Advanced(editorial));
        page.Children.Add(Advanced(Indented(Card("How much", null, null,
                Slider(ctx, 0, 1, 0.01, SettingsCopy.Percent, s => s.EditorialAmount, (s, v) => s with { EditorialAmount = Math.Round(v, 2) })))
            .ShowWhen(ctx, s => s.EditorialTypography)));

        // ---- Colour ----
        Section(page, "Colour");
        var colour = Card("Colour", null, "",
            Combo(ctx, LyricStyleCopy.ColourChoices, s => s.ColourMode.RawValue(), (s, v) => s with { LyricColourMode = v }));
        ctx.OnRefresh(s => colour.Description = LyricStyleCopy.ColourDetail(s.ColourMode));
        page.Children.Add(colour);
        page.Children.Add(RememberedNote(ctx, Playing, StyleAspect.Colour));
        page.Children.Add(Indented(Card("Your colours", null, null, CustomColours(ctx)))
            .ShowWhen(ctx, s => s.ColourMode == LyricColourMode.CustomGradient));

        // Advanced colour
        page.Children.Add(Advanced(Card("Colour strength", LyricStyleCopy.ColourBacking, "",
            Slider(ctx, LyricColourOptions.StrengthMin, LyricColourOptions.StrengthMax, 0.01, SettingsCopy.Percent,
                s => s.LyricColourStrength, (s, v) => s with { LyricColourStrength = Math.Round(v, 2) }, "Neutral", "Vivid"))));
        page.Children.Add(Advanced(Card("Colour speed", null, "",
                Slider(ctx, LyricColourOptions.SpeedMin, LyricColourOptions.SpeedMax, 0.01, SettingsCopy.Percent,
                    s => s.LyricColourSpeed, (s, v) => s with { LyricColourSpeed = Math.Round(v, 2) }, "Slow", "Lively"))
            .ShowWhen(ctx, s => s.ColourMode.Drifts())));
        var highlight = Card("Highlight colour", null, "",
            Combo(ctx, SettingsCopy.HighlightModes, s => s.LyricHighlightMode, (s, v) => s with { LyricHighlightMode = v }));
        ctx.OnRefresh(s => highlight.Description = (SettingsCopy.HighlightModes.FirstOrDefault(c => c.Value == s.LyricHighlightMode)?.Detail ?? "")
            + " For Cutout labels, tiles and Cascade’s held words.");
        page.Children.Add(Advanced(highlight));
        page.Children.Add(Advanced(Indented(Card("Your highlight colour", "Text on a tile always keeps 4.5:1 contrast.", null, HighlightPicker(ctx)))
            .ShowWhen(ctx, s => s.LyricHighlightMode == "custom")));

        // ---- Word motion ----
        Section(page, "Word motion");
        var revealToggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0 };
        var reveal = Card("Reveal sung words", LyricStyleCopy.RevealSungWords, "", revealToggle);
        ctx.OnRefresh(s =>
        {
            revealToggle.IsOn = s.RevealsWordsForControls();
            var reason = LyricStyleCopy.RevealLockReason(s.RevealLockForControls());
            revealToggle.IsEnabled = reason is null;
            reveal.Description = reason is null ? LyricStyleCopy.RevealSungWords : $"{LyricStyleCopy.RevealSungWords} {reason}";
            AutomationProperties.SetHelpText(revealToggle, reveal.Description);
        });
        revealToggle.Toggled += (_, _) =>
        {
            if (revealToggle.IsEnabled) ctx.Update(s => s.WithRevealsWordsForControls(revealToggle.IsOn));
        };
        page.Children.Add(reveal);
        var entrance = new ComboBox { MinWidth = 260 };
        var entranceCard = Card("Entrance", null, "", entrance);
        string[] shownFor = [""];
        ctx.OnRefresh(s =>
        {
            // The menu marks entrances the chosen layout leaves out, so it is rebuilt when the layout changes.
            if (shownFor[0] != s.LyricComposition)
            {
                shownFor[0] = s.LyricComposition;
                entrance.Items.Clear();
                foreach (var choice in SettingsCopy.Entrances(s.LyricComposition))
                    entrance.Items.Add(new ComboBoxItem { Content = choice.Title, Tag = choice.Value });
            }
            var current = s.EntranceForControls();
            entrance.SelectedIndex = AppSettings.WordArrivals.ToList().IndexOf(current);
            var parts = new List<string>
            {
                s.LyricComposition == Automatic ? "For the layouts Custom Mix picks." : "For the layout chosen above.",
            };
            if (EntranceSupport.Note(WordArrivals.Parse(current), SettingsCopy.Kind(s.LyricComposition), s.MixEntrances()) is { } note) parts.Add(note);
            if (reduceMotion()) parts.Add(SettingsCopy.ReduceMotionEntrances);
            entranceCard.Description = string.Join(" ", parts);
            AutomationProperties.SetHelpText(entrance, entranceCard.Description);
        });
        entrance.SelectionChanged += (_, _) =>
        {
            if (entrance.SelectedItem is ComboBoxItem { Tag: string value }) ctx.Update(s => s.WithEntranceForControls(value));
        };
        page.Children.Add(entranceCard);
        page.Children.Add(Expander("Mixed entrances", "Each word takes one of the entrances ticked here.", "", MixList(ctx), expanded: true)
            .ShowWhen(ctx, s => s.EntranceForControls() == "mix"));
        // Word symbols and action words (the Mac's ad5a6a6 and cba108f).
        page.Children.Add(Card("Symbols beside listed words", "Love, fire, home… in English lines timed by word.", "",
            Toggle(ctx, s => s.WordSymbolsEnabled, (s, v) => s with { WordSymbolsEnabled = v })));
        page.Children.Add(Card("Animate action words", "Spin, fall, freeze… move as they are sung.", "",
            Toggle(ctx, s => s.ActionWordsEnabled, (s, v) => s with { ActionWordsEnabled = v })));
        // The Mac's "Action word strength" (16 Nov 2026), under its switch and hidden while it is off.
        SettingsChoice[] actionStrengths =
            [.. Enum.GetValues<ActionWordStrength>().Select(strength => new SettingsChoice(strength.RawValue(), strength.Title()))];
        page.Children.Add(Indented(Card("Action word strength", "How big their motion is.", "",
                Combo(ctx, actionStrengths, s => ActionWordStrengths.Parse(s.ActionWordsStrength).RawValue(),
                    (s, v) => s with { ActionWordsStrength = v }, width: 180)))
            .ShowWhen(ctx, s => s.ActionWordsEnabled));

        // Advanced word motion
        page.Children.Add(Advanced(Card("Faint next-word preview", LyricStyleCopy.NextWordPreview, "",
            Toggle(ctx, s => s.PreviewsNextWordForControls(), (s, v) => s.WithPreviewsNextWordForControls(v)))));
        page.Children.Add(Advanced(Indented(Card("Preview lead", "How long before each word its preview appears.", null,
                Slider(ctx, AppSettings.PreviewLeadMin, AppSettings.PreviewLeadMax, 0.05, LyricStyleCopy.Seconds,
                    s => s.NextWordPreviewLead, (s, v) => s with { NextWordPreviewLead = Math.Round(v / 0.05) * 0.05 },
                    spoken: LyricStyleCopy.SpokenPreviewLead)))
            .ShowWhen(ctx, s => s.PreviewsNextWordForControls())));
        page.Children.Add(Advanced(SettingsKit.Expander("Symbol words", "The words that take a symbol, and which.", "",
            WordListEditor.Build(ctx, "word symbol list", s => s.WordSymbolsWords, (s, v) => s with { WordSymbolsWords = v },
                WordAccents.DefaultSymbols, WordListEditor.SymbolChoices, "star",
                WordAccents.EnglishKey, value => value.Trim(), "Only English words (letters a–z and digits) can take a symbol."))));
        page.Children.Add(Advanced(SettingsKit.Expander("Action words", "The words that move, and how.", "",
            WordListEditor.Build(ctx, "action word list", s => s.ActionWordsWords, (s, v) => s with { ActionWordsWords = v },
                WordAccents.DefaultActions.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value.Name())).ToList(),
                WordListEditor.ActionChoices, "spin",
                WordAccents.ActionKey, value => WordAccents.ParseAction(value)?.Name(), "Type a word made of letters or digits."))));

        // ---- Instrumental breaks ----
        Section(page, "Instrumental breaks");
        page.Children.Add(Card("Glowing orb during breaks", null, "",
            Toggle(ctx, s => s.ShowInterludes, (s, v) => s with { ShowInterludes = v })));
        // The orb's style (the Mac's 13b8ed6): thinking-orbs' nine states, or Mix, with a live 64 pt preview.
        var orbPreview = new OrbPreview(() => ctx.Current.ThinkingOrbStyle, reduceMotion);
        SettingsChoice[] orbStyles =
        [
            new("listening", "Listening"), new("searching", "Searching"), new("solving", "Solving"), new("working", "Working"),
            new("weaving", "Weaving"), new("connecting", "Connecting"), new("composing", "Composing"), new("shaping", "Shaping"),
            new("breathing", "Breathing"), new("mix", "Mix"),
        ];
        var orbPicker = Combo(ctx, orbStyles,
            s => ThinkingOrbStage.StyleValues.Contains(s.ThinkingOrbStyle ?? "") ? s.ThinkingOrbStyle! : "listening",
            (s, v) => s with { ThinkingOrbStyle = v }, width: 180);
        ctx.OnRefresh(_ => orbPreview.Refresh());
        var orbControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        orbControls.Children.Add(orbPreview);
        orbControls.Children.Add(orbPicker);
        page.Children.Add(Indented(Card("Orb style", "Mix picks a different one for each break.", null,
            orbControls)).ShowWhen(ctx, s => s.ShowInterludes));

        // Advanced instrumental breaks (moved from Timing: it decides when a break is shown).
        var pauses = Card("Treat long pauses as breaks", null, "",
            Toggle(ctx, s => s.RepairQQGaps, (s, v) => s with { RepairQQGaps = v }));
        ctx.OnRefresh(s => pauses.Description = SettingsCopy.PauseCaption(s.ShowInterludes));
        page.Children.Add(Advanced(pauses));
        return page;
    }

    /// <summary>The song playing: its query once its length is known (what a style is remembered for), and its artist.</summary>
    private readonly record struct PlayingSong(Core.Lyrics.LyricQuery? Song, string? Artist);

    /// <summary>
    /// Under the Layout or Colour picker while a style remembered for the music playing overrides it: what it uses, with
    /// Update (the choice above, remembered there instead) and Forget (this part of the memory dropped). The Mac's
    /// RememberedStyleNote.
    /// </summary>
    private static Card RememberedNote(SettingsContext ctx, Func<PlayingSong> playing, StyleAspect aspect)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var card = Indented(Card("Remembered", LyricStyleCopy.RememberedDetail, null, buttons));
        (RememberedStyle Style, StyleScope Scope, Core.Lyrics.LyricQuery Song)? shown = null;
        var update = Button("Update", () =>
        {
            if (shown is not { } active) return;
            ctx.Update(s =>
            {
                var style = aspect == StyleAspect.Layout ? active.Style with { Composition = s.LyricComposition }
                    : active.Style with { Colour = s.ColourMode.RawValue() };
                return StyleMemory.Remember(s, style, active.Song, active.Scope);
            });
        });
        var forget = Button("Forget", () =>
        {
            if (shown is { } active) ctx.Update(s => StyleMemory.ForgetPart(s, active.Song, active.Scope, active.Style, aspect));
        });
        buttons.Children.Add(update);
        buttons.Children.Add(forget);
        ctx.OnRefresh(s =>
        {
            var now = playing();
            shown = null;
            if (now.Song is { } song && StyleMemory.Remembered(s, song, now.Artist) is { } active
                && StyleMemory.RememberedName(aspect, active.Style) is { } name)
            {
                shown = (active.Style, active.Scope, song);
                card.Title = LyricStyleCopy.RememberedTitle(active.Scope, name);
                var scope = StyleMemory.ScopeName(active.Scope);
                AutomationProperties.SetName(update, $"Remember the choice above for this {scope} instead");
                AutomationProperties.SetName(forget, $"Use the choice above for this {scope} too");
            }
            card.Visibility = shown is null ? Visibility.Collapsed : Visibility.Visible;
        });
        return card;
    }

    /// <summary>
    /// What is remembered for the song playing, or buttons to remember the choices above for it or its artist, and Forget
    /// all (the Mac's LyricStyleMemorySection).
    /// </summary>
    private static void AddMemory(StackPanel page, SettingsContext ctx, Func<PlayingSong> playing)
    {
        Core.Lyrics.LyricQuery? song = null;
        (RememberedStyle Style, StyleScope Scope)? active = null;

        // A style remembered for the song playing: what it uses, Update and Forget.
        var activeButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var activeCard = Card("This song uses", LyricStyleCopy.MemoryInUse, null, activeButtons);
        activeButtons.Children.Add(Button("Update with the choices above", () =>
        {
            if (song is { } now && active is { } kept) ctx.Update(s => StyleMemory.Remember(s, StyleMemory.Chosen(s), now, kept.Scope));
        }));
        activeButtons.Children.Add(Button("Forget", () =>
        {
            if (song is { } now && active is { } kept) ctx.Update(s => StyleMemory.Forget(s, now, kept.Scope));
        }));
        page.Children.Add(activeCard);

        // Nothing remembered yet: remember the choices above for the song or its artist.
        var rememberButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var rememberCard = Card("Remember this style", LyricStyleCopy.MemorySaves, null, rememberButtons);
        rememberButtons.Children.Add(Button("For this song", () =>
        {
            if (song is { } now) ctx.Update(s => StyleMemory.Remember(s, StyleMemory.Chosen(s), now, StyleScope.Song));
        }, "Remember for this song"));
        var forArtist = Button("For the artist", () =>
        {
            if (song is { } now) ctx.Update(s => StyleMemory.Remember(s, StyleMemory.Chosen(s), now, StyleScope.Artist));
        });
        rememberButtons.Children.Add(forArtist);
        page.Children.Add(rememberCard);

        var nothing = Caption(LyricStyleCopy.MemoryNothingPlaying);
        nothing.Margin = new Thickness(1, 2, 0, 2);
        page.Children.Add(nothing);

        // Forget all, after a question.
        var confirm = new StackPanel { Spacing = 12, MaxWidth = 320 };
        var question = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var flyout = new Flyout { Content = confirm };
        confirm.Children.Add(question);
        confirm.Children.Add(Button("Forget all", () =>
        {
            flyout.Hide();
            ctx.Update(StyleMemory.ForgetAll);
        }, "Forget every remembered style"));
        var forgetAll = new Button { Content = "Forget all", Flyout = flyout };
        AutomationProperties.SetName(forgetAll, "Forget every remembered style");
        var countCard = Card("Remembered styles", null, null, forgetAll);
        page.Children.Add(countCard);

        ctx.OnRefresh(s =>
        {
            var now = playing();
            song = now.Song;
            active = song is null ? null : StyleMemory.Remembered(s, song, now.Artist);
            activeCard.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
            rememberCard.Visibility = song is not null && active is null ? Visibility.Visible : Visibility.Collapsed;
            nothing.Visibility = song is null ? Visibility.Visible : Visibility.Collapsed;
            if (active is { } kept)
            {
                activeCard.Title = $"This {StyleMemory.ScopeName(kept.Scope)} uses {StyleMemory.Describe(kept.Style)}";
                activeCard.Description = LyricStyleCopy.MemoryInUse;
            }
            var artist = song?.Artist ?? "";
            forArtist.Content = string.IsNullOrWhiteSpace(artist) ? "For the artist" : $"For {artist}";
            AutomationProperties.SetName(forArtist, string.IsNullOrWhiteSpace(artist) ? "Remember for the artist" : $"Remember for {artist}");
            forArtist.IsEnabled = !string.IsNullOrWhiteSpace(artist);
            rememberCard.Description = string.IsNullOrWhiteSpace(artist) && song is not null
                ? $"{LyricStyleCopy.MemorySaves} {LyricStyleCopy.MemoryNoArtist}" : LyricStyleCopy.MemorySaves;
            int songs = s.StyleMemorySongs?.Count ?? 0, artists = s.StyleMemoryArtists?.Count ?? 0;
            countCard.Visibility = songs + artists > 0 ? Visibility.Visible : Visibility.Collapsed;
            countCard.Description = StyleMemory.RememberedCount(songs, artists);
            question.Text = $"Forget every remembered style? {StyleMemory.RememberedCount(songs, artists)}. Those songs and artists then use the choices above.";
        });
    }

    /// <summary>
    /// The custom gradient's colours: a swatch per colour that opens Windows' colour picker, a remove button while there
    /// are more than two, and Add colour up to four.
    /// </summary>
    private static StackPanel CustomColours(SettingsContext ctx)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var shown = "";
        ctx.OnRefresh(s =>
        {
            var colours = LyricColourOptions.Sanitised(s.LyricColourCustom ?? []);
            var key = string.Join(",", colours);
            if (key == shown) return;
            shown = key;
            row.Children.Clear();
            for (var i = 0; i < colours.Count; i++)
            {
                var index = i;
                var hex = colours[i];
                var swatch = new Border
                {
                    Width = 28, Height = 20, Style = Resource<Style>("SettingsSwatch"), Background = new SolidColorBrush(Parse(hex)),
                };
                var button = new Button { Content = swatch };
                AutomationProperties.SetName(button, $"Colour {index + 1}");
                AutomationProperties.SetItemStatus(button, hex);
                ToolTipService.SetToolTip(button, hex);
                var picker = new ColorPicker
                {
                    IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorSliderVisible = true, IsColorChannelTextInputVisible = true,
                    IsHexInputVisible = true, ColorSpectrumShape = ColorSpectrumShape.Ring, Color = Parse(hex),
                };
                AutomationProperties.SetName(picker, $"Colour {index + 1}");
                var flyout = new Flyout { Content = picker, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
                button.Flyout = flyout;
                // Saved when the picker closes, so dragging across the spectrum doesn't write a file per step.
                flyout.Closed += (_, _) =>
                {
                    var c = picker.Color;
                    ctx.Update(s => s.WithCustomColour(index, $"#{c.R:X2}{c.G:X2}{c.B:X2}"));
                };
                row.Children.Add(button);
                if (colours.Count > 2)
                {
                    var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(8, 6, 8, 6) };
                    AutomationProperties.SetName(remove, $"Remove colour {index + 1}");
                    ToolTipService.SetToolTip(remove, "Remove this colour");
                    remove.Click += (_, _) => ctx.Update(s => s.WithCustomColourRemoved(index));
                    row.Children.Add(remove);
                }
            }
            if (colours.Count < 4)
            {
                var add = Button("Add colour", () => ctx.Update(s => s.WithCustomColourAdded()));
                ToolTipService.SetToolTip(add, "Add another colour to the gradient");
                row.Children.Add(add);
            }
        });
        return row;
    }

    /// <summary>A number box per layout in the rotation (0–5 songs in a row).</summary>
    private static StackPanel RotationList(SettingsContext ctx)
    {
        var list = new StackPanel { Spacing = 6, Padding = new Thickness(40, 4, 0, 4) };
        foreach (var layout in LayoutRotation.Order)
        {
            var title = SettingsCopy.Kind(layout)?.Title() ?? layout;
            var count = new TextBlock
            {
                Style = Resource<Style>("SettingsCaption"),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 120,
            };
            var box = new NumberBox
            {
                Minimum = 0, Maximum = LayoutRotation.MaximumCount, SmallChange = 1, LargeChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 130,
            };
            AutomationProperties.SetName(box, $"{title}: songs in a row");
            ctx.OnRefresh(s =>
            {
                var n = (s.RotateLayoutCounts ?? new Dictionary<string, int>()).TryGetValue(layout, out var c) ? c : 1;
                box.Value = n;
                count.Text = SettingsCopy.RotationCount(n);
            });
            box.ValueChanged += (_, e) =>
            {
                if (!double.IsFinite(e.NewValue)) return;
                var n = (int)Math.Clamp(Math.Round(e.NewValue), 0, LayoutRotation.MaximumCount);
                ctx.Update(s => s with
                {
                    RotateLayoutCounts = new Dictionary<string, int>(s.RotateLayoutCounts ?? new Dictionary<string, int>()) { [layout] = n },
                });
            };
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Style = Resource<Style>("SettingsTitle") };
            row.Children.Add(name);
            Grid.SetColumn(count, 1);
            row.Children.Add(count);
            Grid.SetColumn(box, 2);
            row.Children.Add(box);
            list.Children.Add(row);
        }
        return list;
    }

    /// <summary>A check box per entrance a custom mix can hold.</summary>
    private static StackPanel MixList(SettingsContext ctx)
    {
        var list = new StackPanel { Spacing = 0, Padding = new Thickness(40, 0, 0, 4) };
        foreach (var choice in SettingsCopy.MixChoices)
        {
            var box = new CheckBox { Content = choice.Title };
            ctx.OnRefresh(s => box.IsChecked = (s.WordEntranceMix ?? []).Contains(choice.Value));
            box.Checked += (_, _) => ctx.Update(s => s.WithMixEntrance(choice.Value, true));
            box.Unchecked += (_, _) => ctx.Update(s => s.WithMixEntrance(choice.Value, false));
            list.Children.Add(box);
        }
        list.Children.Add(Caption("With none ticked, each layout uses its own entrance."));
        return list;
    }

    /// <summary>The installed families, filled when first shown.</summary>
    private static ComboBox FontList(SettingsContext ctx, Func<IReadOnlyList<string>> installedFonts)
    {
        var combo = new ComboBox { MinWidth = 260, MaxWidth = 360, PlaceholderText = "Select a family" };
        var filled = false;
        void Fill(AppSettings s)
        {
            if (!filled && s.FontChoice == "installed")
            {
                filled = true;
                foreach (var family in installedFonts()) combo.Items.Add(family);
            }
            var index = combo.Items.IndexOf(s.FontFamily);
            if (index < 0 && !string.IsNullOrWhiteSpace(s.FontFamily))
            {
                combo.Items.Insert(0, s.FontFamily);
                index = 0;
            }
            combo.SelectedIndex = index;
        }
        ctx.OnRefresh(Fill);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string family) ctx.Update(s => s with { FontFamily = family });
        };
        return combo;
    }

    /// <summary>A swatch button that opens Windows' colour picker for the custom highlight.</summary>
    private static Button HighlightPicker(SettingsContext ctx)
    {
        var swatch = new Border { Width = 40, Height = 20, Style = Resource<Style>("SettingsSwatch") };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        content.Children.Add(swatch);
        content.Children.Add(label);
        var button = new Button { Content = content };
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorSliderVisible = true, IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true, ColorSpectrumShape = ColorSpectrumShape.Ring,
        };
        AutomationProperties.SetName(picker, "Your highlight colour");
        // Opens leftwards from the swatch at the row's right edge, so the picker stays inside the window.
        var flyout = new Flyout { Content = picker, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        button.Flyout = flyout;
        ctx.OnRefresh(s =>
        {
            var colour = Parse(s.LyricHighlightCustom);
            swatch.Background = new SolidColorBrush(colour);
            label.Text = s.LyricHighlightCustom;
            AutomationProperties.SetItemStatus(button, s.LyricHighlightCustom);
            picker.Color = colour;
        });
        // Saved when the picker closes, so dragging across the spectrum doesn't write a file per step.
        flyout.Closed += (_, _) =>
        {
            var c = picker.Color;
            ctx.Update(s => s with { LyricHighlightCustom = $"#{c.R:X2}{c.G:X2}{c.B:X2}" });
        };
        return button;

    }

    private static Windows.UI.Color Parse(string hex)
    {
        var text = (hex ?? "").Trim().TrimStart('#');
        if (text.Length == 6 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return Windows.UI.Color.FromArgb(255, 0xFF, 0x92, 0xA3);
    }
}
