// Ported from the Mac app's LyricTextDecoding.swift (Lunalyr's own code). The Mac orders the legacy encodings with
// Foundation's language-aware detection, which .NET does not have; here the lossless legacy decodings are ordered by
// how much of their CJK text is everyday writing (kana, frequent Han characters, frequent Hangul syllables), since a
// file decoded in the wrong legacy encoding comes out as rare characters, half-width kana or private-use code points.

using System.Text;
using System.Text.RegularExpressions;

namespace Lunalyr.Core.Lyrics.Parsing;

public enum LyricDecodeFailure
{
    TooLarge,
    Unopenable,
    Unreadable,
    NoTimestamps,
}

/// <summary>Text with usable lyric timing, or why a file can't be used.</summary>
public sealed record LyricDecodeResult(string? Text, LyricDecodeFailure? Failure)
{
    public bool Succeeded => Text is not null;

    public static string Message(LyricDecodeFailure failure) => failure switch
    {
        LyricDecodeFailure.TooLarge => "This file is too large to be a lyric file.",
        LyricDecodeFailure.Unopenable => "This file couldn’t be opened.",
        LyricDecodeFailure.Unreadable => "This file’s text encoding isn’t supported. Save it as UTF-8 and try again.",
        _ => "This file has no usable LRC or TTML timestamps.",
    };
}

/// <summary>
/// Reads lyric files in the encodings players and editors commonly save: UTF-8 and UTF-16 (with or without a
/// byte-order mark), Shift_JIS, GB18030, Big5 and EUC-KR. Of the decodings that succeed without loss, the first to
/// yield usable LRC or TTML timing wins.
/// </summary>
public static partial class LyricTextDecoder
{
    /// <summary>Text with usable lyric timing, or why the file can't be used.</summary>
    public static LyricDecodeResult Lyrics(ReadOnlySpan<byte> data, int limit = 1_000_000)
    {
        if (data.Length > limit) return new(null, LyricDecodeFailure.TooLarge);
        var texts = Decodings(data);
        if (texts.Count == 0) return new(null, LyricDecodeFailure.Unreadable);
        var usable = texts.FirstOrDefault(HasTiming);
        return usable is null ? new(null, LyricDecodeFailure.NoTimestamps) : new(usable, null);
    }

    /// <summary>The most plausible text, preferring a decoding with lyric timing.</summary>
    public static string? Decode(ReadOnlySpan<byte> data)
    {
        var texts = Decodings(data);
        if (texts.Count <= 1) return texts.FirstOrDefault();
        return texts.FirstOrDefault(HasTiming) ?? texts[0];
    }

    /// <summary>Lossless decodings, most plausible first, each prepared (no byte-order mark, XML declaration saying UTF-8).</summary>
    public static IReadOnlyList<string> Decodings(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return [];
        // A byte-order mark is unambiguous.
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Strict(new UTF8Encoding(false, true), data[3..]) is { } utf8 ? [Prepared(utf8)] : [];
        }
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            var encoding = new UnicodeEncoding(bigEndian: data[0] == 0xFE, byteOrderMark: false, throwOnInvalidBytes: true);
            return Strict(encoding, data[2..]) is { } utf16 ? [Prepared(utf16)] : [];
        }
        // UTF-16 without a mark can also be valid UTF-8, full of NULs, so the zero-byte pattern is checked before
        // UTF-8 is trusted.
        var order = Utf16Order(data);
        var utf8Text = Strict(new UTF8Encoding(false, true), data);
        if (order is null && utf8Text is not null) return [Prepared(utf8Text)];
        var found = new List<string>();
        void Add(string? text)
        {
            if (text is null) return;
            var value = Prepared(text);
            if (!found.Contains(value)) found.Add(value);
        }
        if (order is { } bigEndian) Add(Strict(new UnicodeEncoding(bigEndian, false, true), data));
        Add(utf8Text);
        var legacy = new List<(string Text, double Score, int Rank)>();
        for (var rank = 0; rank < LegacyCodePages.Length; rank++)
        {
            // Windows maps bytes these encodings leave undefined to private-use characters; Foundation refuses them.
            if (Legacy(LegacyCodePages[rank]) is { } encoding && Strict(encoding, data) is { } text && !text.Any(c => c is >= '' and <= ''))
            {
                legacy.Add((text, Plausibility(text), rank));
            }
        }
        foreach (var item in legacy.OrderByDescending(item => item.Score).ThenBy(item => item.Rank)) Add(item.Text);
        return found;
    }

    /// <summary>Whether the text parses as LRC or TTML with at least one timed line.</summary>
    public static bool HasTiming(string text)
    {
        if (text.TrimStart().StartsWith('<')) return TtmlParser.Parse(text, 0, ["en"]).Count > 0;
        return LrcParser.Parse(text, 0).Count > 0;
    }

    /// <summary>Shift_JIS, GB18030, Big5, EUC-KR.</summary>
    private static readonly int[] LegacyCodePages = [932, 54936, 950, 51949];

    private static Encoding? Legacy(int codePage)
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Strict(Encoding encoding, ReadOnlySpan<byte> data)
    {
        try
        {
            return encoding.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>UTF-16 without a mark leaves a zero in every other byte of the ASCII timestamps; which half holds them gives the byte order.</summary>
    private static bool? Utf16Order(ReadOnlySpan<byte> data)
    {
        var sample = data[..Math.Min(512, data.Length)];
        if (sample.Length < 8) return null;
        int even = 0, odd = 0;
        for (var index = 0; index < sample.Length; index++)
        {
            if (sample[index] != 0) continue;
            if (index % 2 == 0) even++;
            else odd++;
        }
        var half = sample.Length / 2;
        if (odd * 3 > half && even * 10 < half) return false; // little-endian
        if (even * 3 > half && odd * 10 < half) return true; // big-endian
        return null;
    }

    [GeneratedRegex("""^(<\?xml[^>]*?encoding\s*=\s*)(["'])[^"']*(["'])""", RegexOptions.CultureInvariant)]
    private static partial Regex XmlEncoding();

    /// <summary>Unicode text with no byte-order mark, and an XML declaration that no longer names the file's original encoding.</summary>
    private static string Prepared(string text)
    {
        var value = text.StartsWith('﻿') ? text[1..] : text;
        if (value.StartsWith("<?xml", StringComparison.Ordinal)) value = XmlEncoding().Replace(value, "${1}${2}UTF-8${3}", 1);
        return value;
    }

    /// <summary>The share of a decoding's non-ASCII characters that are everyday writing; punctuation does not count.</summary>
    internal static double Plausibility(string text)
    {
        int common = 0, total = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if (v < 0x80 || v is >= 0x3000 and <= 0x303F or >= 0xFF01 and <= 0xFF5E or >= 0x2000 and <= 0x206F) continue;
            if (Rune.IsWhiteSpace(rune)) continue;
            total++;
            if (v is >= 0x3041 and <= 0x30FA || v == 0x30FC || CommonCharacters.Contains(v)) common++;
        }
        return total == 0 ? 0 : (double)common / total;
    }

    /// <summary>Frequent Han characters (simplified, traditional and Japanese forms) and frequent Hangul syllables.</summary>
    private static readonly HashSet<int> CommonCharacters = BuildCommon(
        "的一是不了人我在有他这中大来上个国到说们为子和你地出道也时年得就那要下以生会自着去之过家学对可她里后小么心多天而能好都然没日于起还发成事只作当想看文无开手十用主行方又如前所本见经头面公同三已老从动两长知民样现分将外但身些与高意进把法此实回二理美点月明其种声全工己话儿者向情部正名定女问力机给等几很业最间新什打便位因重被走电四第门相次东政海口使教西再平真听世气信北少关并内加化由却代军产入先山五太水万市眼体别处总才场师书比住员九笑性通目华报立马命张活难神数件安表原车白应路期叫死常提感金何更反合放做系计或司利受光王果亲界及今京务制解各任至清物台象记边共风战干接它许八特觉望直服毛林题建南度统色字请交爱让认算论百吃义科怎元社术结六功指思非流每青管夫连远资队跟带花快条院变联言权往展该领传近留红治决周保达办运武半候七必城父强步完革深区即求品士转量空甚众技轻程告江语英基派满式李息写呢识极令黄德收脸钱党倒未持取设始版双历越史商千片容研像找友孩站广改议形委早房音火际则首单据导影失拿网香似斯专石若兵弟谁校读志飞观争究包组造落视济喜离虽坏兴切团依终演细树杀尔夜梦泪雨星歌唱温暖寂寞永远等待离开回忆微笑哭泣孤单拥抱幸福未来天空云阳春夏秋冬河湖灯街城市季节思念心跳承诺勇敢放手忘记相信希望梦想时间世界爱情感谢晚安早安今天明天昨天" +
        "們個這說來時會對國過還後當經麼樣學點裡開愛問關見實發現頭話無長聽讓應從東車間電動聲書歡氣邊記親請種歲幾變萬難識風許師業處進體別機總寫議員結條統帶將爾買復飛歷華軍視節區錢則單場盡權濟義讀誰離壞興團終殺夢淚雲陽燈街節憶擁抱幸福孤單寂寞溫暖遠戀與愛為麼們嗎齊兩隻聽聲們網憶擁遺忘記號題顏頁" +
        "僕君私夢空声涙世界今日時人手目光明言見行来生恋好前気思出何会歌花風雨星月色未沈溶駆消届笑泣抱願想夜心愛桜春夏秋冬朝昼晩誰彼女男子供友達心臓胸痛優強弱歩走飛終始続待帰" +
        "이다는의에가고하을를지기서도한사그로나있니어요리자것아들수해대만시게보내일인정면주라무우제습까없했같너날랑마음처난오늘눈물말몰알았모든좋싶네와함께계속길꿈밤별빛바람손잡억세상떠돌언영원히린미안워행복웃슬픔아픔려생각줘걸거야될때봐금원르할많첫번잊을래던운나의널건데듯된거든요줄것도저냥왜더잘살꼭못너무까지면서부터처럼잖니까보다또반짝여기저기");

    private static HashSet<int> BuildCommon(string characters) => [.. characters.EnumerateRunes().Select(rune => rune.Value)];
}
