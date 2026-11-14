// Icons for lyric words (the Mac's LyricWordIcons.swift, Build 88; Lunalyr's own code, no line from the Verso import,
// ported as it is). The tables keep the Mac's SF Symbol names, so both apps agree on which words have a picture;
// SegoeIcons maps each name to the Segoe Fluent Icons glyph Windows draws (chosen by looking at the font, see
// docs/compositions/stage.md for where no fitting glyph exists).

using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Compositions;

/// <summary>
/// A small monochrome icon for a lyric word with a clear picture of its own ("love" a heart, "home" a house, "call" a
/// phone), or null. Most words have none, and the commonest ("you", "me", "the") never do: the icon marks a word, it is
/// not decoration. Matching folds case, punctuation, apostrophes and simple inflections (loves, loved, loving, lovin',
/// lover all read as "love"), and takes a few common Chinese, Japanese and Korean words on a glued run's whole text.
/// </summary>
public static class LyricWordIcons
{
    /// <summary>The SF Symbol name for <paramref name="word"/> (as the Mac has it), or null when it has no clear one.</summary>
    public static string? Symbol(string word) => Entry(word)?.Symbol;

    /// <summary>
    /// Whether <paramref name="word"/>'s icon is a strong one: a thing you can picture (a heart, a house) rather than a
    /// person or a verb (she, run).
    /// </summary>
    public static bool IsStrong(string word) => Entry(word)?.Strong ?? false;

    public static (string Symbol, bool Strong)? Entry(string word)
    {
        foreach (var key in Candidates(word))
        {
            if (Strong.TryGetValue(key, out var symbol)) return (symbol, true);
            if (Weak.TryGetValue(key, out symbol)) return (symbol, false);
        }
        return null;
    }

    /// <summary><paramref name="word"/> lowercased, without punctuation or apostrophes, then the stems its simple inflections fold back to, most literal first.</summary>
    public static List<string> Candidates(string word)
    {
        var lowered = word.ToLowerInvariant();
        var kept = new StringBuilder(lowered.Length);
        foreach (var rune in lowered.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            // The Mac's CharacterSet.letters (L* and M*) and decimalDigits.
            if (category is >= UnicodeCategory.UppercaseLetter and <= UnicodeCategory.EnclosingMark
                || category == UnicodeCategory.DecimalDigitNumber)
                kept.Append(rune.ToString());
        }
        var @base = kept.ToString();
        if (@base.Length == 0) return [];
        var result = new List<string> { @base };
        // Chinese, Japanese and Korean: a word with its particle (夜空に, 밤에) reads as the word.
        if (!@base.All(char.IsAscii))
        {
            var elements = new List<string>();
            var enumerator = StringInfo.GetTextElementEnumerator(@base);
            while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
            var stem = elements.ToList();
            static bool Hiragana(string element) => element.EnumerateRunes().All(rune => rune.Value is >= 0x3041 and <= 0x3096);
            while (stem.Count > 1 && Hiragana(stem[^1])) stem.RemoveAt(stem.Count - 1);
            if (stem.Count != elements.Count) result.Add(string.Concat(stem));
            string[] particles = ["에", "이", "가", "을", "를", "은", "는", "의", "도", "로"];
            if (elements.Count > 1 && particles.Contains(elements[^1])) result.Add(string.Concat(elements.Take(elements.Count - 1)));
            return result;
        }
        void Add(string candidate)
        {
            if (candidate.Length < 2 || result.Contains(candidate)) return;
            result.Add(candidate);
        }
        foreach (var suffix in (ReadOnlySpan<string>)["ing", "in", "ers", "er", "ed", "es", "s"])
        {
            if (!@base.EndsWith(suffix, StringComparison.Ordinal) || @base.Length <= suffix.Length + 1) continue;
            var stem = @base[..^suffix.Length];
            Add(stem);
            // loving → love; danced → dance (only a stem of three or more, so "seed" never reads as "see").
            if (stem.Length >= 3) Add(stem + "e");
            // running → run; dropped → drop.
            if (stem.Length >= 3 && stem[^2] == stem[^1] && !"aeiou".Contains(stem[^1])) Add(stem[..^1]);
        }
        // cried, cries, skies → cry, sky.
        foreach (var suffix in (ReadOnlySpan<string>)["ied", "ies"])
            if (@base.EndsWith(suffix, StringComparison.Ordinal) && @base.Length > 4) Add(@base[..^3] + "y");
        return result;
    }

    /// <summary>Pictures: a layout may show these one after another.</summary>
    public static readonly Dictionary<string, string> Strong = new()
    {
        ["love"] = "heart.fill", ["heart"] = "heart.fill", ["lovely"] = "heart.fill",
        ["home"] = "house.fill", ["house"] = "house.fill",
        ["call"] = "phone.fill", ["phone"] = "iphone", ["ring"] = "phone.fill",
        ["night"] = "moon.fill", ["moon"] = "moon.fill", ["midnight"] = "moon.fill", ["tonight"] = "moon.fill",
        ["sun"] = "sun.max.fill", ["sunshine"] = "sun.max.fill", ["morning"] = "sun.max.fill", ["summer"] = "sun.max",
        ["star"] = "star.fill",
        ["rain"] = "cloud.rain.fill", ["storm"] = "cloud.bolt.rain.fill", ["thunder"] = "cloud.bolt.fill", ["lightning"] = "bolt.fill",
        ["fire"] = "flame.fill", ["burn"] = "flame.fill", ["flame"] = "flame.fill",
        ["heartbreak"] = "heart.slash.fill", ["heartbroken"] = "heart.slash.fill", ["broken"] = "heart.slash.fill",
        ["car"] = "car.fill", ["drive"] = "car.fill",
        ["road"] = "road.lanes", ["highway"] = "road.lanes", ["street"] = "road.lanes",
        ["sea"] = "water.waves", ["ocean"] = "water.waves", ["wave"] = "water.waves", ["river"] = "water.waves",
        ["music"] = "music.note", ["song"] = "music.note", ["sing"] = "music.note", ["melody"] = "music.note", ["radio"] = "radio.fill",
        ["dance"] = "figure.dance",
        ["money"] = "dollarsign", ["cash"] = "dollarsign", ["dollar"] = "dollarsign",
        ["clock"] = "clock.fill",
        ["city"] = "building.2.fill", ["town"] = "building.2.fill",
        ["sky"] = "cloud.fill", ["cloud"] = "cloud.fill",
        ["snow"] = "snowflake", ["winter"] = "snowflake", ["cold"] = "snowflake", ["ice"] = "snowflake",
        ["tear"] = "drop.fill", ["cry"] = "drop.fill",
        ["dream"] = "moon.zzz.fill", ["sleep"] = "moon.zzz.fill",
        ["world"] = "globe", ["earth"] = "globe",
        ["text"] = "message.fill", ["message"] = "message.fill", ["letter"] = "envelope.fill",
        ["eye"] = "eye.fill",
        ["hand"] = "hand.raised.fill",
        ["fly"] = "airplane", ["plane"] = "airplane",
        ["bird"] = "bird.fill", ["wing"] = "bird.fill",
        ["flower"] = "camera.macro", ["rose"] = "camera.macro", ["garden"] = "leaf.fill", ["tree"] = "tree.fill", ["leaf"] = "leaf.fill",
        ["key"] = "key.fill", ["door"] = "door.left.hand.open", ["window"] = "window.vertical.closed",
        ["bed"] = "bed.double.fill", ["gift"] = "gift.fill", ["crown"] = "crown.fill", ["king"] = "crown.fill", ["queen"] = "crown.fill",
        ["diamond"] = "diamond.fill", ["gold"] = "sparkles", ["photo"] = "photo.fill", ["picture"] = "photo.fill",
        ["camera"] = "camera.fill", ["book"] = "book.fill", ["game"] = "gamecontroller.fill", ["party"] = "party.popper.fill",
        ["wind"] = "wind", ["mountain"] = "mountain.2.fill", ["island"] = "beach.umbrella.fill", ["beach"] = "beach.umbrella.fill",
        ["lock"] = "lock.fill", ["chain"] = "link", ["bell"] = "bell.fill", ["clown"] = "theatermasks.fill", ["mask"] = "theatermasks.fill",
        ["coffee"] = "cup.and.saucer.fill", ["wine"] = "wineglass.fill", ["drink"] = "wineglass.fill", ["champagne"] = "wineglass.fill",
        ["smoke"] = "smoke.fill", ["candle"] = "flame.fill", ["angel"] = "sparkles", ["magic"] = "wand.and.stars",
        ["train"] = "tram.fill", ["boat"] = "sailboat.fill", ["ship"] = "ferry.fill",
        ["cat"] = "cat.fill", ["dog"] = "dog.fill",
        ["rainbow"] = "rainbow", ["earthquake"] = "waveform.path",
        // Chinese, Japanese and Korean, on a glued run's whole text.
        ["愛"] = "heart.fill", ["恋"] = "heart.fill", ["爱"] = "heart.fill", ["사랑"] = "heart.fill", ["사랑해"] = "heart.fill",
        ["家"] = "house.fill", ["집"] = "house.fill",
        ["夜"] = "moon.fill", ["夜空"] = "moon.fill", ["밤"] = "moon.fill", ["月"] = "moon.fill", ["달"] = "moon.fill",
        ["雨"] = "cloud.rain.fill", ["비"] = "cloud.rain.fill",
        ["星"] = "star.fill", ["별"] = "star.fill",
        ["夢"] = "moon.zzz.fill", ["梦"] = "moon.zzz.fill", ["꿈"] = "moon.zzz.fill",
        ["花"] = "camera.macro", ["꽃"] = "camera.macro",
        ["海"] = "water.waves", ["바다"] = "water.waves",
        ["雪"] = "snowflake", ["눈물"] = "drop.fill", ["涙"] = "drop.fill", ["泪"] = "drop.fill",
        ["空"] = "cloud.fill", ["하늘"] = "cloud.fill", ["太陽"] = "sun.max.fill", ["太阳"] = "sun.max.fill",
        ["火"] = "flame.fill", ["歌"] = "music.note", ["노래"] = "music.note",
    };

    /// <summary>People and verbs: shown more sparingly.</summary>
    public static readonly Dictionary<string, string> Weak = new()
    {
        ["her"] = "figure.stand.dress", ["she"] = "figure.stand.dress", ["girl"] = "figure.stand.dress", ["woman"] = "figure.stand.dress",
        ["his"] = "figure.stand", ["he"] = "figure.stand", ["him"] = "figure.stand", ["boy"] = "figure.stand", ["man"] = "figure.stand",
        ["time"] = "clock.fill", ["light"] = "lightbulb.fill", ["day"] = "sun.max.fill",
        ["see"] = "eye.fill", ["look"] = "eye.fill", ["watch"] = "eye.fill",
        ["run"] = "figure.run", ["walk"] = "figure.walk", ["kiss"] = "mouth.fill", ["talk"] = "bubble.left.fill",
    };
}

/// <summary>
/// The Segoe Fluent Icons glyph Windows draws for each of the Mac's SF Symbols in <see cref="LyricWordIcons"/> and
/// Portal's ornaments. Chosen by looking at the font (Windows 11's <c>SegoeIcons.ttf</c>), keeping the meaning; where
/// it has no fitting glyph the closest meaningful one stands in, and a symbol mapped to null leaves its word without
/// an icon (the words still show, just bare).
/// </summary>
public static class SegoeIcons
{
    /// <summary>The font the glyphs are drawn from (Windows 11; Windows 10 has most of them in Segoe MDL2 Assets).</summary>
    public const string Family = "Segoe Fluent Icons";
    public const string FallbackFamily = "Segoe MDL2 Assets";

    public static readonly Dictionary<string, string?> Glyphs = new()
    {
        ["heart.fill"] = "\uEB52",
        ["house.fill"] = "\uEA8A",
        ["phone.fill"] = "\uE626",
        ["iphone"] = "\uE8EA",
        ["moon.fill"] = "\uF1DB",
        ["sun.max.fill"] = "\uE706",
        ["sun.max"] = "\uE706",
        ["star.fill"] = "\uE735",
        // No cloud with rain: the cloud with drops falling from it.
        ["cloud.rain.fill"] = "\uEA91",
        // No storm cloud: the lightning bolt.
        ["cloud.bolt.rain.fill"] = "\uE945",
        ["cloud.bolt.fill"] = "\uE945",
        ["bolt.fill"] = "\uE945",
        ["flame.fill"] = "\uECAD",
        ["heart.slash.fill"] = "\uEA92",
        ["car.fill"] = "\uE804",
        // No road: the road sign (a turn in a diamond).
        ["road.lanes"] = "\uE816",
        ["water.waves"] = "\uEDA8",
        ["music.note"] = "\uEC4F",
        // No radio: the broadcast mast.
        ["radio.fill"] = "\uEC05",
        // No dancing figure (the only figure with raised arms is Accessibility's): the music note.
        ["figure.dance"] = "\uEC4F",
        // No coin or banknote: a dollar sign, set from the text face by font fallback.
        ["dollarsign"] = "$",
        ["clock.fill"] = "\uE917",
        ["building.2.fill"] = "\uEC06",
        ["cloud.fill"] = "\uE753",
        // No snowflake: the thermometer (cold).
        ["snowflake"] = "\uE9CA",
        ["drop.fill"] = "\uEB42",
        // Windows' own sleep sign: the Quiet hours crescent.
        ["moon.zzz.fill"] = "\uE708",
        ["globe"] = "\uE909",
        ["message.fill"] = "\uED0C",
        ["envelope.fill"] = "\uE715",
        ["eye.fill"] = "\uE890",
        // No raised hand: a hand.
        ["hand.raised.fill"] = "\uF271",
        ["airplane"] = "\uEB4C",
        // No bird.
        ["bird.fill"] = null,
        // No flower: the leaves.
        ["camera.macro"] = "\uEC0A",
        ["leaf.fill"] = "\uEC0A",
        ["tree.fill"] = "\uEC0A",
        ["key.fill"] = "\uE8D7",
        ["door.left.hand.open"] = "\uF3B1",
        ["window.vertical.closed"] = "\uE737",
        // No bed.
        ["bed.double.fill"] = null,
        ["gift.fill"] = "\uE719",
        // No crown.
        ["crown.fill"] = null,
        ["diamond.fill"] = "\uF178",
        ["sparkles"] = "\uE794",
        ["photo.fill"] = "\uE91B",
        ["camera.fill"] = "\uE722",
        ["book.fill"] = "\uE82D",
        ["gamecontroller.fill"] = "\uE7FC",
        // No party popper: sparkles.
        ["party.popper.fill"] = "\uE794",
        // No wind: the wavy lines (as the sea's).
        ["wind"] = "\uEDA8",
        // No mountain, beach umbrella, theatre masks, smoke, cats, dogs, rainbow or mouth.
        ["mountain.2.fill"] = null,
        ["beach.umbrella.fill"] = null,
        ["lock.fill"] = "\uE72E",
        ["link"] = "\uE71B",
        ["bell.fill"] = "\uF2A5",
        ["theatermasks.fill"] = null,
        ["cup.and.saucer.fill"] = "\uEC32",
        // No wine glass: the cup (a drink).
        ["wineglass.fill"] = "\uEC32",
        ["smoke.fill"] = null,
        ["wand.and.stars"] = "\uE794",
        ["tram.fill"] = "\uEB4D",
        ["sailboat.fill"] = "\uEB48",
        ["ferry.fill"] = "\uEB48",
        ["cat.fill"] = null,
        ["dog.fill"] = null,
        ["rainbow"] = null,
        // An earthquake: the waveform.
        ["waveform.path"] = "\uF61F",
        // People: no figure in a dress or standing figure; the contact silhouette for both.
        ["figure.stand.dress"] = "\uE77B",
        ["figure.stand"] = "\uE77B",
        ["lightbulb.fill"] = "\uEA80",
        ["figure.run"] = "\uE726",
        ["figure.walk"] = "\uE805",
        ["mouth.fill"] = null,
        ["bubble.left.fill"] = "\uE8F2",
        // Portal's ornaments.
        ["arrow.up.right"] = "\uE8AD",
        ["arrow.right"] = "\uE72A",
        ["sparkle"] = "\uF4A5",
        ["play.fill"] = "\uF5B0",
        ["plus"] = "\uE710",
    };

    /// <summary>The glyph for <paramref name="symbol"/> (an SF Symbol name), or null when Windows shows none.</summary>
    public static string? For(string? symbol) => symbol is not null && Glyphs.TryGetValue(symbol, out var glyph) ? glyph : null;
}
