using Lunalyr.Core.Compositions;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>
/// The redone action words (the Mac's 16 Nov 2026 work, transfer/mac-2026-11-16 7497a2b): the 18 actions, the wider
/// vocabulary, other languages, chorus repeats, the motion's envelope and Reduce Motion, and the symbol's entrance.
/// Ported from WordAccentsTests.swift with the same inputs and expected results.
/// </summary>
public class WordActionsLivelyTests
{
    private static WordAction? ActionIn(string line)
    {
        var runs = line.Split(' ');
        return WordAccents.Plan(runs, symbols: false, actions: true, WordAccentLists.Defaults).Select(a => a.Action).FirstOrDefault(a => a is not null);
    }

    private static WordAction? Action(string word) => WordAccents.ActionFor(word, WordAccentLists.Defaults);

    // Ported from WordAccentsTests.swift: testPluralsAndIngForms (the action half)
    [Fact]
    public void Plurals_and_ing_forms_reach_their_action()
    {
        Assert.Equal(WordAction.Spin, Action("spinning"));
        Assert.Equal(WordAction.Dip, Action("falling"));
        Assert.Equal(WordAction.Dip, Action("fallin'"));
        Assert.Equal(WordAction.Dip, Action("dropped"));
        Assert.Equal(WordAction.Grow, Action("rising"));
        Assert.Equal(WordAction.Grow, Action("rises"));
        Assert.Equal(WordAction.Float, Action("flying"));
        Assert.Equal(WordAction.Shake, Action("shivering"));
        Assert.Equal(WordAction.Frost, Action("freezing"));
        Assert.Equal(WordAction.Frost, Action("frozen"));
        Assert.Equal(WordAction.Hop, Action("jumped"));
        Assert.Equal(WordAction.Dissolve, Action("vanished"));
    }

    // Ported from WordAccentsTests.swift: testDefaultListsAreTheDecidedOnes (the action half)
    [Fact]
    public void The_default_action_list_is_the_decided_one()
    {
        var keys = WordAccents.DefaultActions.Select(pair => pair.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(WordAction.Spin, WordAccentLists.Defaults.Actions["spin"]);
        Assert.Equal(WordAction.Frost, WordAccentLists.Defaults.Actions["cold"]);
        Assert.Equal(WordAccents.AllActions.ToHashSet(), WordAccents.DefaultActions.Select(pair => pair.Value).ToHashSet());
        Assert.Equal(18, WordAccents.AllActions.Count);
        Assert.All(keys, key => Assert.Equal(key, WordAccents.EnglishKey(key)));
    }

    // Ported from WordAccentsTests.swift: testOneActionWordALine
    [Fact]
    public void One_action_word_a_line()
    {
        var plan = WordAccents.Plan(["we", "spin", "and", "fall", "and", "rise"], false, true, WordAccentLists.Defaults);
        Assert.Single(plan, accent => accent.Action is not null);
        Assert.Equal(WordAction.Spin, plan[1].Action);
    }

    // Ported from WordAccentsTests.swift: testAWordCanTakeBoth
    [Fact]
    public void A_word_can_take_both()
    {
        var plan = WordAccents.Plan(["set", "the", "fire"], true, true, WordAccentLists.Defaults);
        Assert.Equal(new RunAccent("flame", WordAction.Flicker), plan[2]);
    }

    [Fact]
    public void Every_action_has_the_Macs_name_and_title()
    {
        string[] names = ["spin", "float", "dip", "shake", "hop", "grow", "shrink", "dissolve", "flicker", "frost", "bleed", "dark",
                          "shatter", "melt", "echo", "pulse", "wave", "sink"];
        string[] titles = ["Spin", "Float", "Dip", "Shake", "Hop", "Grow", "Shrink", "Dissolve", "Burn", "Freeze", "Bleed", "Dark",
                           "Shatter", "Melt", "Echo", "Heartbeat", "Wave", "Sink"];
        Assert.Equal(names, WordAccents.AllActions.Select(a => a.Name()));
        Assert.Equal(titles, WordAccents.AllActions.Select(a => a.Title()));
    }

    // Ported from WordAccentsTests.swift: testSavedListsReadOldNamesAndSkipDroppedActions
    [Fact]
    public void Saved_lists_read_old_names_and_skip_dropped_actions()
    {
        Assert.Equal(WordAction.Frost, WordAccents.ParseAction("frost"));
        Assert.Equal(WordAction.Frost, WordAccents.ParseAction("Freeze"));
        Assert.Equal(WordAction.Flicker, WordAccents.ParseAction(" warm flicker "));
        Assert.Equal(WordAction.Pulse, WordAccents.ParseAction("Heartbeat"));
        Assert.Equal(WordAction.Dissolve, WordAccents.ParseAction("dissolve"));
        foreach (var gone in new[] { "scream", "explode", "glow", "whisper", "ghost", "electric", "", "nonsense" })
            Assert.Null(WordAccents.ParseAction(gone));
        var stored = new Dictionary<string, string> { ["scream"] = "scream", ["chill"] = "Freeze", ["spin"] = "spin", ["Girar"] = "spin", ["飛ぶ"] = "float" };
        var actions = new WordAccentLists([], WordAccents.CleanActions(stored)).Actions;
        Assert.False(actions.ContainsKey("scream"), "a dropped action is skipped");
        Assert.Equal(WordAction.Frost, actions["chill"]);
        Assert.Equal(WordAction.Spin, actions["girar"]);
        Assert.Equal(WordAction.Float, actions["飛ぶ"]);
        Assert.Equal("scream", stored["scream"]);
    }

    // Ported from WordAccentsTests.swift: testTheActionListEditorKeepsWordsInAnyLanguage
    [Fact]
    public void The_action_list_keeps_words_in_any_language()
    {
        Assert.Equal("spinning", WordAccents.ActionKey("Spinning"));
        Assert.Equal("tombe", WordAccents.ActionKey("Tombé"));
        Assert.Equal("飛ぶ", WordAccents.ActionKey("飛ぶ"));
        Assert.Null(WordAccents.ActionKey("  !! "));
    }

    // The Mac's fold() results, measured on the Mac (16 Nov 2026).
    [Theory]
    [InlineData("frío", "frio")]
    [InlineData("Flügel", "flugel")]
    [InlineData("Straße", "strasse")]
    [InlineData("đ", "đ")]
    [InlineData("が", "が")]
    [InlineData("한글", "한글")]
    [InlineData("Ａ", "a")]
    [InlineData("Tombé", "tombe")]
    [InlineData("L'OBSCURITÉ", "lobscurite")]
    public void Fold_matches_the_Macs(string word, string folded) => Assert.Equal(folded, ActionLexicon.Fold(word));

    // Ported from WordAccentsTests.swift: testRepeatsAlternateFullAndGentle
    [Fact]
    public void Repeats_alternate_full_and_gentle()
    {
        IReadOnlyList<string>?[] lines =
        [
            ["we", "spin", "around"], ["then", "we", "fall"], ["we", "spin", "again"], ["no", "action", "here"],
            ["spinning", "spinning"], ["and", "we", "spin"], ["we", "fall"],
        ];
        var gentle = WordAccents.GentleLines(lines, WordAccentLists.Defaults);
        // spin: 1st full, 2nd gentle, 3rd full, 4th gentle; fall: 1st full, 2nd gentle.
        Assert.Equal([2, 5, 6], gentle.Order());
        Assert.Equal(gentle, WordAccents.GentleLines(lines, WordAccentLists.Defaults));
        var plan = WordAccents.Plan(lines[2]!, false, true, WordAccentLists.Defaults, gentle: true);
        Assert.Equal(WordAction.Spin, plan[1].Action);
        Assert.True(plan[1].Gentle, "a gentle repeat still takes the line's one action");
        Assert.False(WordAccents.Plan(lines[2]!, false, true, WordAccentLists.Defaults)[1].Gentle);
        Assert.False(WordAccents.Plan(lines[2]!, false, true, WordAccentLists.Defaults, gentle: true)[0].Gentle, "only the action word");
    }

    // The Mac's gentleLines read from a song's lines as every layout reads them (rolling glue, backing vocals lifted):
    // a bracketed backing "(spin)" is never the lead's action, and a word glued from syllables (spin·ning) is one word.
    [Fact]
    public void A_songs_repeats_are_read_from_its_lead_words()
    {
        static Lunalyr.Core.Lyrics.LyricLine Line(double start, params string[] texts)
        {
            var words = texts.Select((text, i) => new Lunalyr.Core.Lyrics.LyricWord(text, start + i * 0.4, start + i * 0.4 + 0.3)).ToList();
            return new Lunalyr.Core.Lyrics.LyricLine
            {
                Start = start, End = start + 3, Text = string.Join(" ", texts).Replace("spin ning", "spinning", StringComparison.Ordinal),
                Words = words, WordTimed = true,
            };
        }
        Lunalyr.Core.Lyrics.LyricLine[] lines =
        [
            Line(0, "we", "spin", "around"),
            Line(4, "(spin)", "we", "fall"),
            Line(8, "spin", "ning", "again"),
            Line(12, "and", "we", "spin"),
        ];
        var breaker = Lunalyr.Core.Lyrics.Readings.SimpleWordBreaker.Instance;
        // spin: line 0 full, line 2 (spinning) gentle, line 3 full; the backing "(spin)" on line 1 is not heard, its
        // "fall" is fall's first.
        Assert.Equal([2], ActionRepeats.Of(lines, WordAccentLists.Defaults, breaker, liftsBacking: true).Order());
        // With backing vocals left in the line, line 1's first action word is its "(spin)": the second spin.
        Assert.Equal([1, 3], ActionRepeats.Of(lines, WordAccentLists.Defaults, breaker, liftsBacking: false).Order());
    }

    // Ported from WordAccentsTests.swift: testOtherLanguagesMatchTheirOwnWords
    [Theory]
    // Spanish
    [InlineData("y vuelvo a caer en tus brazos", WordAction.Dip)]
    [InlineData("el mundo gira sin parar", WordAction.Spin)]
    [InlineData("me quema tu fuego", WordAction.Flicker)]
    [InlineData("tiemblo cuando me miras", WordAction.Shake)]
    [InlineData("la sangre en mis venas", WordAction.Bleed)]
    [InlineData("me hundo en tu mirada", WordAction.Sink)]
    // Portuguese
    [InlineData("eu vou cair de novo", WordAction.Dip)]
    [InlineData("o mar levou meu coração", WordAction.Wave)]
    [InlineData("tudo vai desaparecer", WordAction.Dissolve)]
    // French
    [InlineData("je tombe encore pour toi", WordAction.Dip)]
    [InlineData("mon cœur brûle", WordAction.Flicker)]
    [InlineData("dans l'obscurité", WordAction.Dark)]
    // German
    [InlineData("ich falle durch die Nacht", WordAction.Dip)]
    [InlineData("alles dreht sich", WordAction.Spin)]
    [InlineData("wir brennen heute Nacht", WordAction.Flicker)]
    [InlineData("mein Herz zerbricht", WordAction.Shatter)]
    [InlineData("es schmilzt dahin", WordAction.Melt)]
    // Italian
    [InlineData("io volo via da te", WordAction.Float)]
    [InlineData("cado ancora", WordAction.Dip)]
    [InlineData("il mare ci porta via", WordAction.Wave)]
    // Indonesian, Tagalog
    [InlineData("aku jatuh cinta", WordAction.Dip)]
    [InlineData("hatiku hancur", WordAction.Shatter)]
    [InlineData("nahulog ang loob ko", WordAction.Dip)]
    [InlineData("dugo sa puso", WordAction.Bleed)]
    // Vietnamese
    [InlineData("nước mắt rơi", WordAction.Dip)]
    [InlineData("em như sóng biển", WordAction.Wave)]
    // Japanese
    [InlineData("空を飛んで行く", WordAction.Float)]
    [InlineData("闇の中で", WordAction.Dark)]
    [InlineData("胸が燃えている", WordAction.Flicker)]
    [InlineData("涙が落ちる", WordAction.Dip)]
    // Korean
    [InlineData("어둠 속에서", WordAction.Dark)]
    [InlineData("눈물이 떨어져", WordAction.Dip)]
    [InlineData("파도처럼", WordAction.Wave)]
    [InlineData("피가 흘러", WordAction.Bleed)]
    // Chinese (simplified and traditional)
    [InlineData("我的心跳", WordAction.Pulse)]
    [InlineData("一起飞翔", WordAction.Float)]
    [InlineData("在黑暗中", WordAction.Dark)]
    [InlineData("心碎了", WordAction.Shatter)]
    [InlineData("我們一起飛", WordAction.Float)]
    public void Other_languages_match_their_own_words(string line, WordAction action) => Assert.Equal(action, ActionIn(line));

    // Ported from WordAccentsTests.swift: testOtherLanguagesAvoidNearMisses
    [Theory]
    [InlineData("a hundred salty tombs")]
    [InlineData("spring is here again")]
    [InlineData("it was grandiose")]
    [InlineData("a disparate crowd")]
    [InlineData("I sang it loud")]
    [InlineData("that romp was fun")]
    [InlineData("a vague idea")]
    [InlineData("vuelve a casa")]
    [InlineData("me gustó, gefallen hat es mir")]
    [InlineData("tremendo día")]
    [InlineData("그날 밤")]
    [InlineData("날 사랑해")]
    [InlineData("今回だけ")]
    [InlineData("浪漫的晚上")]
    [InlineData("我暗恋你")]
    [InlineData("沉默的人")]
    [InlineData("影響される")]
    [InlineData("we run to the bay")]
    [InlineData("don't be late")]
    [InlineData("mi bebé")]
    [InlineData("onde você está")]
    [InlineData("se queda conmigo")]
    [InlineData("me llamas")]
    [InlineData("zu laut")]
    [InlineData("i grandi amori")]
    [InlineData("a crescent moon")]
    [InlineData("sangria at noon")]
    [InlineData("olá amigo")]
    [InlineData("커피가 좋아")]
    [InlineData("沈黙の夜")]
    public void Other_languages_avoid_near_misses(string line) => Assert.Null(ActionIn(line));

    // Ported from WordAccentsTests.swift: testAWordTheUserRemovedStaysStill
    [Fact]
    public void A_word_the_user_removed_stays_still()
    {
        var user = WordAccentLists.Defaults.Actions.Where(pair => pair.Key is not ("ocean" or "echo")).ToDictionary();
        foreach (var word in new[] { "ocean", "echo" })
            Assert.Null(ActionLexicon.Match(word, user, new ActionLexicon.Script(word)));
    }

    // Ported from WordAccentsTests.swift: testTheDefaultListCatchesVividWordsOnly
    [Theory]
    [InlineData("we twirl till dawn", WordAction.Spin)]
    [InlineData("I soar above it", WordAction.Float)]
    [InlineData("we dive in", WordAction.Dip)]
    [InlineData("so nervous tonight", WordAction.Shake)]
    [InlineData("let's dance again", WordAction.Hop)]
    [InlineData("watch it bloom", WordAction.Grow)]
    [InlineData("it's all gone now", WordAction.Dissolve)]
    [InlineData("strike a flame", WordAction.Flicker)]
    [InlineData("cold as winter snow", WordAction.Frost)]
    [InlineData("an open wound", WordAction.Bleed)]
    [InlineData("lost in the midnight", WordAction.Dark)]
    [InlineData("in a million pieces", WordAction.Shatter)]
    [InlineData("the candle drips", WordAction.Melt)]
    [InlineData("I hear the voices", WordAction.Echo)]
    [InlineData("I'm alive again", WordAction.Pulse)]
    [InlineData("down by the river", WordAction.Wave)]
    [InlineData("in the deep end", WordAction.Sink)]
    [InlineData("bailando toda la noche", WordAction.Hop)]
    [InlineData("on danse encore", WordAction.Hop)]
    [InlineData("wir tanzen", WordAction.Hop)]
    [InlineData("一起跳舞", WordAction.Hop)]
    [InlineData("踊り続ける", WordAction.Hop)]
    [InlineData("춤을 춰", WordAction.Hop)]
    [InlineData("눈이 내리는 하늘", WordAction.Float)]
    public void The_default_list_catches_vivid_words(string line, WordAction action) => Assert.Equal(action, ActionIn(line));

    [Theory]
    [InlineData("all night long")]
    [InlineData("you have my heart")]
    [InlineData("walking in the rain")]
    [InlineData("so high")]
    [InlineData("a rose for you")]
    [InlineData("a glass of wine")]
    [InlineData("just let it go")]
    [InlineData("멈춤 없이")]
    [InlineData("坐火车回家")]
    public void The_default_list_leaves_everyday_words_still(string line) => Assert.Null(ActionIn(line));

    // Ported from WordAccentsTests.swift: testTheUsersOwnListComesFirst
    [Fact]
    public void The_users_own_list_comes_first()
    {
        var user = new Dictionary<string, WordAction> { ["caer"] = WordAction.Spin, ["fly"] = WordAction.Hop, ["飛"] = WordAction.Sink };
        var script = new ActionLexicon.Script("caer");
        Assert.Equal(WordAction.Spin, ActionLexicon.Match("caer", user, script)?.Action);
        Assert.Equal(WordAction.Hop, ActionLexicon.Match("flying", user, script)?.Action);
        Assert.Equal(WordAction.Sink, ActionLexicon.Match("飛んで", user, new ActionLexicon.Script("飛んで"))?.Action);
    }

    // ---- Motion ----

    // Ported from WordAccentsTests.swift: testReduceMotionIsAGlowOnly
    [Fact]
    public void Reduce_Motion_is_a_glow_only()
    {
        foreach (var action in WordAccents.AllActions)
        {
            var duration = LivelyAction.Duration(action, 1.2);
            foreach (var elapsed in new[] { -1, -0.001, 0, duration, duration + 0.001, 5 })
                Assert.Equal(LivelyPose.Identity, LivelyAction.ReduceMotionCue(action, elapsed, duration));
            for (var step = 0; step <= 100; step++)
            {
                var pose = LivelyAction.ReduceMotionCue(action, duration * step / 100, duration);
                Assert.Equal(0, pose.Dx);
                Assert.Equal(0, pose.Dy);
                Assert.Equal(0, pose.Turn);
                Assert.Equal(0, pose.Tilt);
                Assert.Equal(1, pose.Sx);
                Assert.Equal(1, pose.Sy);
                Assert.Equal(1, pose.Opacity);
                Assert.Equal(0, pose.Twister);
                Assert.Equal(0, pose.Vignette);
                var (r, g, b) = pose.Multiply;
                foreach (var channel in new[] { r, g, b })
                {
                    Assert.True(channel <= 1.0001, $"{action}: multiplying never lightens");
                    Assert.True(channel >= 0.4, $"{action}");
                }
            }
            var mid = LivelyAction.ReduceMotionCue(action, duration * 0.4, duration);
            Assert.True(mid.Glow > 0.3, $"{action} still gives a cue");
            var quiet = LivelyAction.ReduceMotionCue(action, duration * 0.4, duration, gentle: true);
            Assert.True(quiet.Glow < mid.Glow, $"{action}: a chorus repeat glows less");
        }
    }

    // Ported from WordAccentsTests.swift: testReduceMotionSymbolOnlyFades
    [Fact]
    public void Under_Reduce_Motion_the_symbol_only_fades()
    {
        Assert.Equal(Easing.Smoothstep(0.5), WordActionMotion.SymbolOpacity(0.15));
        Assert.Equal(SymbolEntrancePose.Rest, SymbolEntrancePose.Make(WordAction.Spin, SymbolEntrancePose.Duration));
        foreach (var elapsed in new[] { 0.0, 0.2, 0.7, 1.4 })
        {
            Assert.Null(SymbolEntrancePose.Entrance(elapsed, reduceMotion: true));
            Assert.Equal(elapsed, SymbolEntrancePose.Entrance(elapsed, reduceMotion: false));
        }
        Assert.Null(SymbolEntrancePose.Entrance(null, reduceMotion: false));
    }

    // Ported from WordAccentsTests.swift: testStrengthSetsTheAmount (the motion half)
    [Fact]
    public void Wild_moves_more_than_Subtle()
    {
        Assert.Equal([LivelyAction.Subtle, LivelyAction.Middle, LivelyAction.Wild],
                     Enum.GetValues<ActionWordStrength>().Select(s => s.Amount()));
        var wild = LivelyPose.Make(WordAction.Hop, 0.35, 1.55, LivelyAction.Intensity(1, 1.2), 1.2);
        var subtle = LivelyPose.Make(WordAction.Hop, 0.35, 1.55, LivelyAction.Intensity(0.2, 1.2), 1.2);
        Assert.True(Math.Abs(wild.Dy) > 3 * Math.Abs(subtle.Dy));
    }

    // Ported from WordAccentsTests.swift: testLivelyDefaultsToWild
    [Fact]
    public void Lively_defaults_to_Wild() => Assert.Equal(LivelyAction.Wild, LivelyAction.DefaultAmount);

    // Ported from WordAccentsTests.swift: testLivelyMotionIsContinuousAndReturnsToRest
    [Fact]
    public void Lively_motion_is_continuous_and_returns_to_rest()
    {
        static double Turn(LivelyPose p)
        {
            var a = p.Turn % 360;
            return a > 180 ? a - 360 : a < -180 ? a + 360 : a;
        }
        foreach (var action in WordAccents.AllActions)
            foreach (var hold in new[] { 0.3, 0.6, 1.2, 2.5 })
            {
                var d = LivelyAction.Duration(action, hold);
                var a = LivelyAction.Intensity(LivelyAction.Wild, hold);
                LivelyPose Pose(double t) => LivelyPose.Make(action, t, d, a, hold);
                foreach (var t in new[] { 0.002, d - 0.002 })
                {
                    var p = Pose(t);
                    var label = $"{action} hold {hold} t {t}";
                    Assert.True(Math.Abs(p.Dx) <= 0.01, label);
                    Assert.True(Math.Abs(p.Dy) <= 0.01, label);
                    Assert.True(Math.Abs(p.Sx - 1) <= 0.01, label);
                    Assert.True(Math.Abs(p.Sy - 1) <= 0.01, label);
                    Assert.True(Math.Abs(p.Tilt) <= 0.5, label);
                    Assert.True(Math.Abs(Turn(p)) <= 1, label);
                }
                var last = Pose(0);
                for (var step = 1; step <= (int)(d * 240) + 1; step++)
                {
                    var t = step / 240.0;
                    var p = Pose(t);
                    var label = $"{action} hold {hold} t {t}";
                    Assert.True(Math.Abs(p.Dx - last.Dx) < 0.045, label);
                    Assert.True(Math.Abs(p.Dy - last.Dy) < 0.05, label);
                    Assert.True(Math.Abs(p.Sx - last.Sx) < 0.03, label);
                    Assert.True(Math.Abs(p.Sy - last.Sy) < 0.03, label);
                    Assert.True(Math.Abs(p.Tilt - last.Tilt) < 1.5, label);
                    var turnStep = (p.Turn - last.Turn) % 360;
                    Assert.True(Math.Min(Math.Abs(turnStep), 360 - Math.Abs(turnStep)) < 12, label);
                    last = p;
                }
            }
    }

    // Ported from WordAccentsTests.swift: testSpinWhirlwindIsAtRestAtTheEnd
    [Fact]
    public void Spin_whirlwind_is_at_rest_at_the_end()
    {
        foreach (var hold in new[] { 0.3, 0.6, 1.2, 2.5 })
        {
            var d = LivelyAction.Duration(WordAction.Spin, hold);
            var a = LivelyAction.Intensity(LivelyAction.Wild, hold);
            for (var k = 0; k <= 400; k++)
            {
                var t = d - 0.02 + k * 0.00005;
                var pose = LivelyPose.Make(WordAction.Spin, t, d, a, hold);
                Assert.True(pose.Twister < 0.02, $"hold {hold} t {t}");
                Assert.True(pose.Glow < 0.05, $"hold {hold} t {t}");
                Assert.True(LivelyAction.SpinSpeed(t, a, hold, d) < 0.02, $"hold {hold} t {t}");
            }
            var last = 0.0;
            for (var step = 0; step <= (int)(d * 2000); step++)
            {
                var t = step / 2000.0;
                var twister = LivelyPose.Make(WordAction.Spin, t, d, a, hold).Twister;
                Assert.True(Math.Abs(twister - last) < 0.05, $"hold {hold} t {t}");
                last = twister;
            }
        }
    }

    // Ported from WordAccentsTests.swift: testSymbolEntranceStartsAndEndsAtRest
    [Fact]
    public void Symbol_entrance_starts_and_ends_at_rest()
    {
        foreach (var action in new WordAction?[] { null }.Concat(WordAccents.AllActions.Select(a => (WordAction?)a)))
        {
            var start = SymbolEntrancePose.Make(action, 0);
            var soon = SymbolEntrancePose.Make(action, 0.002);
            Assert.True(Math.Abs(start.Dy - soon.Dy) <= 0.01, $"{action}");
            Assert.True(Math.Abs(start.Sx - soon.Sx) <= 0.01, $"{action}");
            Assert.True(Math.Abs(start.Rotation - soon.Rotation) <= 1, $"{action}");
            var late = SymbolEntrancePose.Make(action, SymbolEntrancePose.Duration - 0.002);
            Assert.True(Math.Abs(late.Dy) <= 0.005 && Math.Abs(late.Dx) <= 0.005, $"{action}");
            Assert.True(Math.Abs(late.Sx - 1) <= 0.005 && Math.Abs(late.Sy - 1) <= 0.005, $"{action}");
            Assert.True(Math.Abs(late.Rotation) <= 0.2 && Math.Abs(late.Cool) <= 0.01, $"{action}");
            Assert.Equal(SymbolEntrancePose.Rest, SymbolEntrancePose.Make(action, SymbolEntrancePose.Duration));
            var last = start;
            for (var step = 1; step <= (int)(SymbolEntrancePose.Duration * 240); step++)
            {
                var p = SymbolEntrancePose.Make(action, step / 240.0);
                Assert.True(Math.Abs(p.Sx - last.Sx) < 0.06, $"{action} {step}");
                Assert.True(Math.Abs(p.Dy - last.Dy) < 0.06, $"{action} {step}");
                Assert.True(Math.Abs(p.Rotation - last.Rotation) < 8, $"{action} {step}");
                last = p;
            }
        }
        Assert.Null(SymbolEntrancePose.Entering(SymbolEntrancePose.Duration));
        Assert.Null(SymbolEntrancePose.Entering(-1));
    }

    // Ported from WordAccentsTests.swift: testLivelyLetterCurvesAreContinuous
    [Fact]
    public void Lively_letter_curves_are_continuous()
    {
        foreach (var hold in new[] { 0.3, 1.2, 2.5 })
        {
            (string Name, double D, Func<double, double, double> Curve)[] curves =
            [
                ("hop", LivelyAction.Duration(WordAction.Hop, hold), (t, d) => LivelyAction.Hop(t, 1, hold, d).Dy),
                ("dip", LivelyAction.Duration(WordAction.Dip, hold), (t, d) => LivelyAction.Dip(t, 1, d)),
                ("melt", LivelyAction.Duration(WordAction.Melt, hold), (t, d) => LivelyAction.Melt(t, 0.2, d)),
                ("sink", LivelyAction.Duration(WordAction.Sink, hold), (t, d) => LivelyAction.Sink(t, 0.3, d)),
                ("shrink", LivelyAction.Duration(WordAction.Shrink, hold), (t, d) => LivelyAction.Shrink(t, 1, d)),
            ];
            foreach (var (name, d, curve) in curves)
            {
                Assert.True(Math.Abs(curve(0.002, d)) <= 0.01, name);
                Assert.True(Math.Abs(curve(d - 0.002, d)) <= 0.01, name);
                var last = 0.0;
                for (var step = 1; step <= (int)(d * 240) + 1; step++)
                {
                    var value = curve(step / 240.0, d);
                    Assert.True(Math.Abs(value - last) < 0.05, $"{name} hold {hold} step {step}");
                    last = value;
                }
            }
        }
    }

    [Fact]
    public void Rnd_is_the_Macs_64_bit_hash()
    {
        // Pinned values: the hash mixes in 64-bit wrapping arithmetic, as Swift's Int does on the Mac.
        Assert.All(Enumerable.Range(0, 200), n =>
        {
            var r = LivelyAction.Rnd(11, n, 3);
            Assert.InRange(r, 0, 0.9999);
        });
        Assert.Equal(LivelyAction.Rnd(11, 5, 1), LivelyAction.Rnd(11, 5, 1));
        Assert.NotEqual(LivelyAction.Rnd(11, 5, 1), LivelyAction.Rnd(11, 5, 2));
    }
}
