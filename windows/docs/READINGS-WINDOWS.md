# Readings and translation on Windows

How the Mac's lyric readings (romanisation) and machine translation map onto Windows, what was checked on the
development PC (Windows 11 Home 26200, English only, 12 Nov 2026), and what differs from the Mac.

Code: pure logic in `src/Lunalyr.Core/Lyrics/Readings/`, Windows services in
`src/Lunalyr.Platform.Windows/Readings/` (`WindowsReadingServices.Create()` builds them).

## What the Mac uses and what Windows has

| Mac | Windows | Status on this PC |
| --- | --- | --- |
| `CFStringTokenizer` (ja): Japanese words + Latin transcription | `Windows.Globalization.JapanesePhoneticAnalyzer` (`WindowsJapaneseTokenizer`) | **Reads nothing here** (see below). Falls back to ICU word breaks, kana only. |
| `applyingTransform(.toLatin)` (ICU Any-Latin) | Windows' own ICU, `icu.dll`, `utrans_*` (`IcuTransliterator`) | Works, offline, no install. |
| `applyingTransform(.mandarinToLatin)` (ICU Han-Latin) | `icu.dll` "Han-Latin" | Works: 我们一起走 → wǒ men yī qǐ zǒu. |
| `.stripDiacritics` | NFD, drop non-spacing marks, NFC (pure .NET) | Works. Not ICU Latin-ASCII, which also rewrites ł etc. |
| `enumerateSubstrings(.byWords)` / `NLTokenizer(.word)` | `icu.dll` `ubrk_*` with the CJ and Thai dictionaries (`IcuWordBreaker`) | Works; parts some Japanese differently (below). |
| `NLLanguageRecognizer` | ELS "Microsoft Language Detection" (`elscore.dll`, `ElsLanguageDetector`) | Works, offline, built in. No confidence values. |
| Apple Translation framework | nothing (see Translation) | Not available. |

`icu.dll` exports `utrans_openU`, `utrans_transUChars`, `ubrk_open` and the rest used here (Windows 10 1903+). All
P/Invoke is `LibraryImport` with blittable signatures. A Native AOT publish of a console that builds the services,
romanises Mandarin, Cyrillic and kana, detects a language and round-trips the translation cache ran correctly with no
trim/AOT warnings.

`Windows.Data.Text.WordsSegmenter` and `TextReverseConversionGenerator` were also tried: both resolve to "und" (no
word breaker) for ja/zh/ko here and return nothing useful, so they are not used.

## Japanese: the open question

`JapanesePhoneticAnalyzer` is the only Windows API that reads kanji. Findings:

- Its class is registered single-threaded (STA). From a multi-threaded (MTA) thread, activation fails with
  `0x80040111 CLASS_E_CLASSNOTAVAILABLE`. On an STA thread it activates, so `WindowsJapaneseTokenizer` runs every
  call on one dedicated background STA thread. (AOT build: the availability probe ran and failed over cleanly.)
- On this PC it returns **no words at all**, even for ありがとう. `TextReverseConversionGenerator("ja")` returns nothing
  too. `C:\Windows\IME\IMEJP\DICTS` holds one small file (no IME dictionaries), and there is no
  `LanguageFeatures-Basic-ja-jp` package under `C:\Windows\servicing\Packages`: the Japanese language is not
  installed. The analyzer most likely needs it (Settings → Time & language → Language & region → Add a language →
  Japanese, which brings the Microsoft IME and its dictionary). **Not verified**: installing it would change system
  settings, which the rules forbid. Its reading quality, word split and long-input handling (100-character limit,
  handled by chunking) are therefore untested.
- `WindowsJapaneseTokenizer.IsAvailable` probes once (今日 must come back with a reading). When it is false,
  `WindowsReadingServices` uses `SimpleJapaneseTokenizer` over ICU word breaks: each word is split where kanji meet
  kana, kana are read with ICU Any-Latin, kanji get no reading (never a Mandarin one). Saved readings (from the
  source or `.roma.lrc`) still align on the kana anchors and are shown.

What an ordinary English Windows PC gives without the analyzer (pinned in `JapaneseWithoutAnalyzerTests`):

| Input | Mac | Windows fallback |
| --- | --- | --- |
| ありがとう, ラーメン, コーヒー, ねぇ, ヴァイオリン, もしもし, こんにちは, はしれ, はどこ, そらを, は？, はっ | same | same |
| すごーい | sugooi | su goo i |
| はなれないで | hanarenaide | wa na renaide |
| あいたい | aitai | a itai |
| まって | matte | ma tte |
| だきしめて | dakishimete | da ki shimete |
| 君の名は | kimi no na wa | kimi no wa (君 is a fixed reading; 名 unread) |
| 今日も | kyou mo | mo |
| 思った | omotta | tta |
| 僕は君が好き | boku wa kimi ga suki | wa kimi ga ki |
| ２人で | futari de | ２ de |
| 12月24日 | 12 gatsu 24 ka | same (月/日 after digits are fixed readings) |
| 今日も…想う + saved "kyou mo mata kimi wo omou" | kyou mo mata kimi wo omou | kyou mo mata kimi wo omo u |
| 過ぎてゆく… + saved "sugite yukutokinonaka" (stage runs) | sugite yuku / toki no / naka | su gite yuku / toki no / naka |

The kana splits come from ICU's dictionary not knowing the words; the spaced saved readings come from unread kanji
being phrases of their own. With the analyzer installed these should match the Mac, but that is unverified.

**Owner's call:** to read kanji on PCs without Japanese installed, the app would need its own dictionary and
analyser. Candidates with acceptable licences: UniDic (BSD-3 option; very large), IPAdic/mecab-ipadic (its own
permissive licence, not strictly MIT/BSD; ~50 MB source), Sudachi's dictionary (Apache-2.0; large). JMdict/KANJIDIC
(EDRDG, CC BY-SA) are not acceptable for a closed-source app. Unihan `kJapaneseKun/On` (Unicode licence) is
per-character only and reads compounds wrongly (今日 as kon·nichi), so it is not worth it. Nothing was downloaded.

## Word breaks (ICU vs Apple)

ICU splits 窓辺に座って海を見ていた as 窓辺|に|座|って|海|を|見|てい|た; Apple as 窓辺|に|座っ|て|海|を|見|て|い|た. Effects,
recorded in `WindowsLyricRunReadingTests`:

- Rolling stages: Mac 窓辺に / 座って / 海を / 見ていた; Windows 窓辺に / 座って / 海を / 見 / ていた (てい is not a word
  that `ContinuesPhrase` lets go on from 見).
- Constellations (phrase parting): Mac 窓辺に / 座って海を見ていた; Windows keeps the whole line as one fragment (every
  ICU word after 窓辺 is "light": one character or hiragana only).
- Chinese and Thai part as on the Mac (我们 / 一起 / 走过 / 春天的 / 小路; แมว / นอน / บน / โต๊ะ).
- `EstimatedWords` (line-timed lyrics) cut at ICU word starts: ５時に会おう becomes ５ / 時に / 会 / おう where Apple has
  ５ / 時 / に / 会おう.

## Language detection

ELS returns a ranked list of tags, no confidence: "I will always love you…" → en, "Je t'aime…" → fr, 我爱你… →
zh-Hans, 我們的關係… → zh-Hant, 君の名は… → ja, 사랑해… → ko. Limits seen: Ukrainian (Ти моє кохання назавжди) came back
as bg first. Because there is no confidence to compare with the Mac's 0.6 threshold, `ElsLanguageDetector` treats
alphabetic texts under 12 letters as unknown; Han, kana and Hangul are judged at any length, and fall back to
`ScriptLanguageGuesser` (kana → ja, Chinese-only or Japanese-only characters, Simplified vs Traditional forms) if
ELS gives nothing. Readings only ask it whether Han text is Japanese or Chinese.

## Cantonese

As on the Mac: there is no generated Jyutping, Cantonese lines get readings only from saved readings, and Mandarin
pinyin is never generated for them. Cantonese is detected by the Mac's character evidence (`CantoneseScore`), which
needs no system service.

## Translation

Windows offers no on-device translation API a desktop app can use without cloud calls. Checked:

- Windows SDK 10.0.26100 metadata: no translation namespace (the only "Translat…" types are unrelated: numeral
  systems, XAML transforms, animations).
- Windows App SDK 2.6.5 AI APIs (`Microsoft.Windows.AI.Text`): `LanguageModel` (Phi Silica), `TextSummarizer`,
  `TextRewriter`, `TextToTableConverter`. No translator. `LanguageModel` needs a Copilot+ PC (NPU) and is a general
  model, not a translation engine; not used.
- ELS services on this PC: script detection, language detection, and transliteration only (Simplified ↔ Traditional
  Chinese, Cyrillic/Devanagari/Malayalam/Bengali → Latin, Hangul decomposition).

So `LyricMachineTranslator.SystemTranslator()` returns null and every song is skipped as `Unavailable`. The policy,
line map (same SHA-256 cache key bytes as the Mac), disk cache and coordinator are ported behind `ILyricTranslator`,
so a translator can be plugged in later. Nothing calls the cloud.

## Re-implemented from behaviour (Verso-attributed lines)

- `RomanizationOptions` and the outer `readings` loop and `romanize` dispatch of `LyricRomanization.swift`.
- `makeLine` / `distributeWords` (`EstimatedWords`). The parsing workstream may port the same behaviour; the two should
  be merged.
- `TimedWordKey` / `presentationID` were not ported: `LyricWord` is a value-equal record and is its own key.
- Tests: `testPinyinKeepsOriginalWordTimes`, `testSavedJyutpingIsUsedInsteadOfGeneratingPinyin`,
  `testJapaneseKanaDoesNotFallBackToMandarin` (ReadingAndTTMLTests).
