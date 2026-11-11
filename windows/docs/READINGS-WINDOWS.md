# Readings and translation on Windows

How the Mac's lyric readings (romanisation) and machine translation map onto Windows, what was checked on the
development PC (Windows 11 Home 26200, English only, 12 Nov 2026), and what differs from the Mac.

Code: pure logic in `src/Lunalyr.Core/Lyrics/Readings/` (the Japanese analyser in `Readings/Japanese/`), Windows
services in `src/Lunalyr.Platform.Windows/Readings/` (`WindowsReadingServices.Create()` builds them).

## What the Mac uses and what Windows has

| Mac | Windows | Status on this PC |
| --- | --- | --- |
| `CFStringTokenizer` (ja): Japanese words + Latin transcription | Bundled IPAdic and Lunalyr's own analyser (`DictionaryJapaneseTokenizer`), readings through ICU Any-Latin | Works on every PC, offline, nothing to install (below). |
| `applyingTransform(.toLatin)` (ICU Any-Latin) | Windows' own ICU, `icu.dll`, `utrans_*` (`IcuTransliterator`) | Works, offline, no install. |
| `applyingTransform(.mandarinToLatin)` (ICU Han-Latin) | `icu.dll` "Han-Latin" | Works: 我们一起走 → wǒ men yī qǐ zǒu. |
| `.stripDiacritics` | NFD, drop non-spacing marks, NFC (pure .NET) | Works. Not ICU Latin-ASCII, which also rewrites ł etc. |
| `enumerateSubstrings(.byWords)` / `NLTokenizer(.word)` | Japanese: the bundled analyser (`JapaneseWordBreaker`); everything else: `icu.dll` `ubrk_*` with the CJ and Thai dictionaries (`IcuWordBreaker`) | Works; Japanese now parts as on the Mac (below). |
| `NLLanguageRecognizer` | ELS "Microsoft Language Detection" (`elscore.dll`, `ElsLanguageDetector`) | Works, offline, built in. No confidence values. |
| Apple Translation framework | nothing (see Translation) | Not available. |

`icu.dll` exports `utrans_openU`, `utrans_transUChars`, `ubrk_open` and the rest used here (Windows 10 1903+). All
P/Invoke is `LibraryImport` with blittable signatures. A Native AOT publish of a console that builds the services,
romanises Japanese, Mandarin, Cyrillic and kana, breaks Japanese words, detects a language and round-trips the
translation cache ran correctly with no trim/AOT warnings (12 Nov 2026, again with the bundled dictionary).

`Windows.Data.Text.WordsSegmenter` and `TextReverseConversionGenerator` were also tried: both resolve to "und" (no
word breaker) for ja/zh/ko here and return nothing useful, so they are not used.

## Japanese: the bundled dictionary

**Owner's decision:** bundle a Japanese morphological analyser and dictionary, so kanji readings and Japanese word
breaks work on every PC without the Japanese language pack.

### What is used

- **Dictionary: IPAdic (mecab-ipadic 2.7.0-20070801)**, the standard MeCab dictionary: 392,126 words, 1,316 ×
  1,316 connection costs, MeCab's character categories and unknown-word templates. Taken from the MeCab project's
  own repository (<https://github.com/taku910/mecab>, commit `2fd29256`, folder `mecab-ipadic`), every file checked
  against its git blob hash. Licence: IPAdic's own (NAIST and ICOT), permissive, allows closed-source commercial use,
  and asks for its notice with every copy: it is in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md), which is
  copied next to the app on build and publish.
- **Analyser: Lunalyr's own** (`MorphologicalAnalyzer`, about 200 lines): MeCab's model (a lattice of dictionary and
  unknown-word candidates, the cheapest path by word and connection cost) written from its published description
  and file formats (no MeCab or NMeCab source was opened while writing it; the owner may still wish to credit MeCab,
  BSD option, in the notices). On 300 lyric-style lines, a selection of the Mac tests' Japanese inputs plus lines
  written for the check, its segmentation and readings were identical to MeCab's (NMeCab 0.10.2 with its IPAdic, run
  outside the repo as a check only; it is GPL/LGPL and is not used by the app).
- **Format and size.** `tools/JapaneseDictionaryBuilder` turns the source (EUC-JP CSV and .def files) into Lunalyr's
  dictionary format (sorted surfaces, entries, readings, the connection matrix and the character table, laid out to
  be used in place) and compresses it with Brotli: `Readings/Data/ipadic.lunadic.br`, **4,395,495 bytes** (SHA-256
  `667510f3…fac264`), **15,719,902 bytes** expanded (SHA-256 `465b802d…3394bd`). The build is reproducible (same
  bytes from the same source). It is an embedded resource of `Lunalyr.Platform.Windows`, so it is inside the
  Native AOT exe: **+4.4 MB to the download**. At first use it is expanded once into
  `%LOCALAPPDATA%\Lunalyr\Dictionaries\ipadic-<hash>.lunadic` (written to a temporary name, then moved; older
  versions deleted) and memory-mapped read-only, so later starts only map it and the OS pages in what the analyser
  touches. If the folder cannot be written it is expanded into memory instead. Tests use a folder under `%TEMP%`.
  Measured in the AOT build: services built in about 140–400 ms on the first run (expanding), about 75 ms after.
- To rebuild it: `dotnet run --project tools/JapaneseDictionaryBuilder -c Release -- build <mecab-ipadic folder>
  src/Lunalyr.Platform.Windows/Readings/Data/ipadic.lunadic.br`; `probe <file> <texts>` prints analyses.

**Trade-offs considered.** *Analyser:* the NMeCab packages on NuGet say in their own metadata: LibNMeCab 0.10.2,
LibNMeCab.IpaDicBin 0.10.0 and MeCab.NET 1.0.0 `GPL-2.0-or-later OR LGPL-2.1-or-later`; NMeCabNetStandard 0.7.3 and
MeCab.DotNet 1.2.0 `GPL-2.0-only OR LGPL-2.1-only`; NMeCab 0.6.4 gives no licence expression, only a link (not
followed); Kawazu 1.1.4 gives none and depends on LibNMeCab. All out. Lucene.Net.Analysis.Kuromoji 4.8.0-beta00018
ships a LICENSE.txt (not read; Lucene.NET is an Apache project) and brings the Lucene.NET core and its attribute
system for one tokenizer; it was not tried, and its AOT behaviour was not measured. DotNetG2P.MeCab 1.10.0
(Apache-2.0) is new, built for speech, and needs OpenJTalk's naist-jdic, a separate and larger dictionary (size not
measured). An own analyser is small, AOT-safe by construction and fully under our control. *Dictionary:* IPAdic is
small and its readings are what MeCab users know; UniDic (BSD option) is more modern but larger (not measured);
Sudachi's small dictionary (Apache-2.0) needs a different engine; JMdict/KANJIDIC (CC BY-SA) are not acceptable for a
closed-source app; Unihan per-character readings read compounds wrongly. *Shipping:* uncompressed in the exe would
add 15.7 MB to the download; expanding into memory at every start would cost about 16 MB of private memory and the
decompression each time; a separate file next to the exe would break the single-file AOT app.
Compressed in the exe, expanded once and mapped is the middle way.

### How it is used

- `DictionaryJapaneseTokenizer` (`IJapaneseTokenizer`) gives the morphemes and their readings (the reading field,
  キョウ for 今日, not the pronunciation キョー) in Latin letters through ICU, in the style Apple's tokenizer gives
  (`omo~tsu`, `ne~e`, macrons), which `JapaneseReading` tidies as on the Mac. Where IPAdic's split differs from
  Apple's in ways the Mac's reading rules depend on, the tokens are shaped by rules about the language, not word
  lists: a ー drawn out after hiragana (すごーい) stays in its word; a small kana never begins a word (はくしゅ|して);
  the volitional stays on its verb (行こう, でしょう); 1 or 2 people (２人, 一人, 二人) is one token (hitori,
  futari); an interjection or adverb in っ takes its と (はっと|して); and は or へ beginning a phrase before an adverb,
  a particle or a pronoun of two kana or more, where the dictionary took it into a verb or adjective, is the particle
  (は|もう, は|から, not はも|う, はから); after a particle already apart, what follows is analysed afresh (しあわせ).
- `JapaneseWordBreaker` (`IWordBreaker`) parts text with kana in it by the analyser, an auxiliary verb kept on what
  it inflects (会おう, いた), digits next to Japanese with it (５|時), and Latin words and punctuation by ICU. Text
  without kana (Chinese, Thai, Latin) goes to ICU as before.
- `JapanesePhoneticAnalyzer`, which read nothing without the Japanese language installed (most likely it needs
  the IME dictionary, not verified; on this PC it returned no words even for ありがとう), is removed: the bundled
  dictionary gives the same answers on every PC. If the bundled dictionary could not be opened at all, the services
  fall back to `SimpleJapaneseTokenizer` over ICU word breaks (kana only, kanji unread), pinned in
  `JapaneseWithoutAnalyzerTests`.

### The Mac's tests

All 29 Japanese tests that were skipped run and pass, with the Mac's inputs and expectations, except these, where
IPAdic splits or reads differently (each keeps the Mac's value in a `// Mac:` comment and asserts Windows'). Two are
defensible alternatives (bokura, doushite); the other three are IPAdic getting it wrong (a mis-split with the same
letters, or a misreading):

| Test | Input | Mac | Windows | Why |
| --- | --- | --- | --- | --- |
| WordsTheTokenizerSplitsAfterHaStayWhole | はぐして | hagushite | hagu shite | Mis-split: IPAdic reads はぐ\|し\|て (剥ぐ + する) rather than ハグ + して; same letters, spaced. |
| WordsTheTokenizerSplitsAfterHaStayWhole | へりくつ, へりくつを | herikutsu (wo) | heri kutsu (wo) | Mis-split: 屁理屈 is not in IPAdic in kana; same letters, spaced. |
| FullWidthDigitsCountPeopleAsHalfWidthOnesDo | 僕ら２人 | boku ra futari | bokura futari | Defensible: 僕ら is one IPAdic word. |
| PhraseSpacesOfWordTimedLinesAreKept | えっ どうして なの + source "e doushite nano" | e / dou shite / na no | e / doushite / na no | Defensible: どうして is one IPAdic adverb. |
| SourceReadingsAreShownInHepburn | 雨が降り止むまでは帰れない + Kunrei source "…hu ri ya mu…" | …furiyamu… | …huriyamu… | Misreading: IPAdic reads 降り here as ori (降りる, get off) for furi (降る, fall); generated alone the line reads "ame ga ori yamu made wa kaerenai". The source's hu then matches nothing of the app's and stays as written. |

The word-break assertions that Windows had replaced now hold as on the Mac and are restored: rolling stages 窓辺に /
座って / 海を / 見ていた, constellation 窓辺に / 座って海を見ていた (`ARollingStagePartsAnUnspacedLineIntoItsWords`), and
５時に会おう as ５ / 時 / に / 会おう for estimated words (`FullWidthDigitsCountPeopleAsHalfWidthOnesDo`).

### Limits

- IPAdic is from 2007: newer words, slang and most names are unknown. Unknown kana words are read as written;
  unknown kanji get no reading (never a Mandarin one), as before.
- Readings are the dictionary's most likely ones in context and can be wrong where a kanji has several (降り止む read
  ori yamu, for furi yamu). Poetic readings that lyrics give kanji (運命 sadame, 本気 maji) are never generated;
  they come only from saved or source readings, which still win.
- Kana-only words IPAdic lacks may be split oddly (へりくつ heri kutsu, はぐして hagu shite, へいき e iki at the start of a
  phrase); the Mac's tokenizer has its own such cases, which `JapaneseReading`'s rules were written for.
- The analysis allocates its lattice per call (lyric lines are short); the dictionary is shared, read-only and
  thread-safe.

What an ordinary English Windows PC gave before (and the fallback still gives; pinned in `JapaneseWithoutAnalyzerTests`):

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

## Word breaks (ICU vs Apple)

ICU alone splits 窓辺に座って海を見ていた as 窓辺|に|座|って|海|を|見|てい|た, which kept the constellation whole and set
見 apart from ていた. Japanese lines now go to the bundled analyser (窓辺|に|座っ|て|海|を|見|て|いた), which parts them
as the Mac does; ICU still parts Chinese and Thai as on the Mac (我们 / 一起 / 走过 / 春天的 / 小路; แมว / นอน / บน /
โต๊ะ). Apple's own split was never observed (no Mac here): the Mac's expectations are what is matched.

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
- The two Verso-attributed lines of `JapaneseReading.swift` are the CFStringTokenizer loop (advance, token range); not
  translated, replaced by `IJapaneseTokenizer`.
- `TimedWordKey` / `presentationID` were not ported: `LyricWord` is a value-equal record and is its own key.
- Tests: `testPinyinKeepsOriginalWordTimes`, `testSavedJyutpingIsUsedInsteadOfGeneratingPinyin`,
  `testJapaneseKanaDoesNotFallBackToMandarin` (ReadingAndTTMLTests).
