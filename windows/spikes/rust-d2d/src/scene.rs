//! Scene constants and the fake lyric timeline (spec: docs/SPIKE-SPEC.md).

pub const BASE: u32 = 0x1B1035;
pub const BLOBS: [u32; 4] = [0x7B3FE4, 0xE4572E, 0x2E86AB, 0xF2C14E];
/// Lissajous periods (x, y) in seconds and phases, one per blob (periods within 20-40 s).
pub const BLOB_PATHS: [(f64, f64, f64, f64); 4] = [
    (23.0, 31.0, 0.0, 1.3),
    (29.0, 37.0, 2.1, 0.4),
    (37.0, 22.0, 4.0, 2.9),
    (26.0, 34.0, 5.2, 4.4),
];

pub const WORD_STEP: f64 = 0.450;
pub const HOLD: f64 = 0.900;
pub const REVEAL: f64 = 0.280;
/// "10 px" blur == D2D StandardDeviation 10/3 at 100% scale (coordinator clarification).
pub const BLUR_SIGMA_100: f32 = 10.0 / 3.0;
pub const DIM_OPACITY: f32 = 0.30;
pub const RISE_EM: f32 = 0.12;

pub struct Line {
    pub words: &'static [&'static str],
    /// Separator inserted between words in the laid-out string.
    pub sep: &'static str,
    pub rtl: bool,
}

pub const LINES: [Line; 7] = [
    Line { words: &["The", "night", "is", "young", "and", "the", "city", "is", "ours"], sep: " ", rtl: false },
    Line { words: &["夜空に", "光る", "星を", "数えて"], sep: "", rtl: false },
    Line { words: &["我们", "在", "月光下", "一起", "唱歌"], sep: "", rtl: false },
    Line { words: &["우리는", "밤새", "노래했어"], sep: " ", rtl: false },
    Line { words: &["نحن", "نغني", "تحت", "ضوء", "القمر"], sep: " ", rtl: true },
    Line { words: &["שרים", "יחד", "עד", "הבוקר"], sep: " ", rtl: true },
    Line { words: &["Starlight", "🌙✨", "tonight", "🎶", "forever", "💜"], sep: " ", rtl: false },
];

pub fn line_duration(i: usize) -> f64 {
    LINES[i].words.len() as f64 * WORD_STEP + HOLD
}

pub fn cycle_duration() -> f64 {
    (0..LINES.len()).map(line_duration).sum()
}

/// Time offset at which (0-based) line `i` starts.
pub fn line_start_offset(i: usize) -> f64 {
    (0..i.min(LINES.len())).map(line_duration).sum()
}

/// Returns (line index, seconds since that line started).
pub fn locate(t: f64) -> (usize, f64) {
    let mut tm = t.rem_euclid(cycle_duration());
    for i in 0..LINES.len() {
        let d = line_duration(i);
        if tm < d {
            return (i, tm);
        }
        tm -= d;
    }
    (0, 0.0)
}

/// Builds the UTF-16 string for a line and each word's (start, len) range in UTF-16 units.
pub fn line_text(i: usize) -> (Vec<u16>, Vec<(u32, u32)>) {
    let line = &LINES[i];
    let mut text: Vec<u16> = Vec::new();
    let mut ranges = Vec::new();
    for (k, w) in line.words.iter().enumerate() {
        if k > 0 {
            text.extend(line.sep.encode_utf16());
        }
        let start = text.len() as u32;
        text.extend(w.encode_utf16());
        ranges.push((start, text.len() as u32 - start));
    }
    (text, ranges)
}

pub fn ease_out_cubic(p: f64) -> f64 {
    let q = 1.0 - p.clamp(0.0, 1.0);
    1.0 - q * q * q
}
