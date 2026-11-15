//! Lunalyr for Windows - Milestone 1 spike: Rust + Direct2D/DirectWrite.
//! See docs/SPIKE-SPEC.md. No audio, no media session, no network.

mod attach;
mod render;
mod scene;
mod snapshot;
mod stats;

use std::sync::atomic::{AtomicBool, AtomicIsize, AtomicU32, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;

use windows::Win32::Foundation::*;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Performance::{QueryPerformanceCounter, QueryPerformanceFrequency};
use windows::Win32::System::SystemInformation::GetSystemTimePreciseAsFileTime;
use windows::Win32::System::Threading::{GetCurrentProcess, GetProcessTimes};
use windows::Win32::UI::HiDpi::*;
use windows::Win32::UI::WindowsAndMessaging::*;
use windows::core::w;

use render::{Shared, StatsAccum};

const TIMER_QUIT: usize = 1;
const TIMER_REBUILD: usize = 2;
const WM_APP_RENDER_FAILED: u32 = WM_APP + 1;

static HIDDEN_HWND: AtomicIsize = AtomicIsize::new(0);
static TASKBAR_CREATED: AtomicU32 = AtomicU32::new(0);
static REBUILD_PENDING: AtomicBool = AtomicBool::new(false);
static QUIT_REQUESTED: AtomicBool = AtomicBool::new(false);

pub fn qpc_now() -> i64 {
    let mut v = 0i64;
    unsafe {
        let _ = QueryPerformanceCounter(&mut v);
    }
    v
}

fn filetime_u64(f: FILETIME) -> u64 {
    ((f.dwHighDateTime as u64) << 32) | f.dwLowDateTime as u64
}

/// (creation time, kernel time, user time) in 100 ns units.
pub fn process_times() -> (u64, u64, u64) {
    let (mut c, mut e, mut k, mut u) = (FILETIME::default(), FILETIME::default(), FILETIME::default(), FILETIME::default());
    unsafe {
        let _ = GetProcessTimes(GetCurrentProcess(), &mut c, &mut e, &mut k, &mut u);
    }
    (filetime_u64(c), filetime_u64(k), filetime_u64(u))
}

pub fn ms_since_process_creation() -> f64 {
    let (created, _, _) = process_times();
    let now = filetime_u64(unsafe { GetSystemTimePreciseAsFileTime() });
    now.saturating_sub(created) as f64 / 10_000.0
}

#[derive(Default, Debug)]
pub struct Args {
    pub seconds: Option<f64>,
    pub stats: Option<String>,
    pub fps: Option<f64>,
    pub windowed: bool,
    pub snapshot: Option<String>,
    pub start_line: usize, // 1-based
    // Hidden test flags.
    pub snapshot_after: f64,
    pub layered: bool,
    pub force_legacy: bool,
}

fn parse_args() -> Args {
    let mut a = Args { start_line: 1, snapshot_after: 3.0, ..Default::default() };
    let v: Vec<String> = std::env::args().skip(1).collect();
    let mut i = 0;
    let next = |i: &mut usize| -> String {
        *i += 1;
        v.get(*i).cloned().unwrap_or_else(|| {
            eprintln!("missing value for {}", v[*i - 1]);
            std::process::exit(2)
        })
    };
    while i < v.len() {
        match v[i].as_str() {
            "--seconds" => a.seconds = next(&mut i).parse().ok(),
            "--stats" => a.stats = Some(next(&mut i)),
            "--fps" => a.fps = next(&mut i).parse().ok().filter(|f: &f64| *f > 0.0),
            "--windowed" => a.windowed = true,
            "--snapshot" => a.snapshot = Some(next(&mut i)),
            "--no-playback" => {} // accepted and ignored: the spike never plays anything
            "--start-line" => a.start_line = next(&mut i).parse().unwrap_or(1).clamp(1, scene::LINES.len()),
            "--snapshot-after" => a.snapshot_after = next(&mut i).parse().unwrap_or(3.0),
            "--layered" => a.layered = true,
            "--legacy" => a.force_legacy = true,
            other => eprintln!("ignoring unknown argument {other}"),
        }
        i += 1;
    }
    a
}

/// Absolute path so the render thread's WIC stream is not cwd-sensitive.
fn absolute(p: &str) -> String {
    std::path::absolute(p).map(|p| p.display().to_string()).unwrap_or_else(|_| p.to_string())
}

struct Ui {
    args: Args,
    shared: Arc<Shared>,
    hinst: HINSTANCE,
    windows: Vec<HWND>,
    render: Option<(JoinHandle<()>, Arc<AtomicBool>)>,
    failures: u32,
}

impl Ui {
    /// Stops the render thread while pumping messages (our child windows live in Explorer's
    /// window tree; blocking this thread without pumping can stall the desktop).
    fn stop_render(&mut self) {
        if let Some((h, stop)) = self.render.take() {
            stop.store(true, Ordering::Relaxed);
            while !h.is_finished() {
                pump_messages();
                std::thread::sleep(std::time::Duration::from_millis(1));
            }
            let _ = h.join();
        }
    }

    fn destroy_windows(&mut self) {
        for h in self.windows.drain(..) {
            unsafe {
                let _ = DestroyWindow(h);
            }
        }
    }

    fn build(&mut self) {
        let targets = if self.args.windowed {
            match attach::create_windowed(self.hinst) {
                Ok((h, t)) => {
                    self.windows.push(h);
                    vec![t]
                }
                Err(e) => {
                    eprintln!("[ui] windowed create failed: {e}");
                    QUIT_REQUESTED.store(true, Ordering::Relaxed);
                    unsafe { PostQuitMessage(1) };
                    return;
                }
            }
        } else {
            match attach::attach_all(self.hinst, self.args.layered, self.args.force_legacy) {
                Ok(v) => {
                    let mut ts = Vec::new();
                    for (h, t) in v {
                        self.windows.push(h);
                        ts.push(t);
                    }
                    attach::dump_tree(&self.windows);
                    ts
                }
                Err(e) => {
                    eprintln!("[ui] attach failed: {e}; retrying in 2 s");
                    schedule_rebuild(2000);
                    return;
                }
            }
        };
        for t in &targets {
            eprintln!("[ui] target {}x{} dpi {} {} Hz mode {}", t.w, t.h, t.dpi, t.hz, t.mode);
        }
        let stop = Arc::new(AtomicBool::new(false));
        let shared = self.shared.clone();
        let stop2 = stop.clone();
        let h = std::thread::Builder::new()
            .name("render".into())
            .spawn(move || {
                if let Err(e) = render::run(targets, shared, stop2.clone()) {
                    eprintln!("[render] error: {e}");
                    if !stop2.load(Ordering::Relaxed) {
                        let hidden = HWND(HIDDEN_HWND.load(Ordering::Relaxed) as *mut _);
                        unsafe {
                            let _ = PostMessageW(Some(hidden), WM_APP_RENDER_FAILED, WPARAM(0), LPARAM(0));
                        }
                    }
                }
            })
            .expect("spawn render thread");
        self.render = Some((h, stop));
    }

    fn rebuild(&mut self) {
        eprintln!("[ui] rebuild");
        self.stop_render();
        self.destroy_windows();
        self.build();
    }
}

fn pump_messages() {
    unsafe {
        let mut msg = MSG::default();
        while PeekMessageW(&mut msg, None, 0, 0, PM_REMOVE).as_bool() {
            if msg.message == WM_QUIT {
                QUIT_REQUESTED.store(true, Ordering::Relaxed);
                continue;
            }
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}

pub fn schedule_rebuild(ms: u32) {
    let hidden = HWND(HIDDEN_HWND.load(Ordering::Relaxed) as *mut _);
    unsafe {
        SetTimer(Some(hidden), TIMER_REBUILD, ms, None);
    }
}

extern "system" fn hidden_proc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    unsafe {
        let tc = TASKBAR_CREATED.load(Ordering::Relaxed);
        if tc != 0 && msg == tc {
            eprintln!("[ui] TaskbarCreated (Explorer restarted) -> re-attach");
            schedule_rebuild(500);
            return LRESULT(0);
        }
        match msg {
            WM_DISPLAYCHANGE | WM_SETTINGCHANGE | WM_DPICHANGED => {
                eprintln!("[ui] msg 0x{msg:04x} -> rebuild");
                schedule_rebuild(500);
                LRESULT(0)
            }
            WM_APP_RENDER_FAILED => {
                schedule_rebuild(250);
                LRESULT(0)
            }
            WM_TIMER => {
                match wp.0 {
                    TIMER_QUIT => {
                        QUIT_REQUESTED.store(true, Ordering::Relaxed);
                        PostQuitMessage(0);
                    }
                    TIMER_REBUILD => {
                        let _ = KillTimer(Some(hwnd), TIMER_REBUILD);
                        REBUILD_PENDING.store(true, Ordering::Relaxed);
                    }
                    _ => {}
                }
                LRESULT(0)
            }
            _ => DefWindowProcW(hwnd, msg, wp, lp),
        }
    }
}

fn main() {
    let args = parse_args();
    unsafe {
        // The manifest already declares PMv2; this fails harmlessly if it is already set.
        let _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
    let mut freq = 0i64;
    unsafe {
        let _ = QueryPerformanceFrequency(&mut freq);
    }
    let origin = qpc_now();
    let shared = Arc::new(Shared {
        qpc_origin: origin,
        qpc_freq: freq as f64,
        time_offset: scene::line_start_offset(args.start_line - 1),
        fps_cap: args.fps,
        snapshot: args.snapshot.as_deref().map(|p| (absolute(p), args.snapshot_after)),
        snapshot_done: AtomicBool::new(false),
        stats: Mutex::new(StatsAccum::default()),
    });
    eprintln!("[ui] {args:?}");

    unsafe {
        let hinst: HINSTANCE = GetModuleHandleW(None).unwrap().into();
        let wc = WNDCLASSEXW {
            cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
            lpfnWndProc: Some(hidden_proc),
            hInstance: hinst,
            lpszClassName: w!("LunalyrSpikeRsHidden"),
            ..Default::default()
        };
        RegisterClassExW(&wc);
        attach::register_class(hinst);

        // Hidden *top-level* window (message-only windows don't get broadcasts like TaskbarCreated).
        let hidden = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            w!("LunalyrSpikeRsHidden"),
            w!("Lunalyr spike (rust-d2d) control"),
            WS_POPUP,
            0,
            0,
            1,
            1,
            None,
            None,
            Some(hinst),
            None,
        )
        .expect("hidden window");
        HIDDEN_HWND.store(hidden.0 as isize, Ordering::Relaxed);
        TASKBAR_CREATED.store(RegisterWindowMessageW(w!("TaskbarCreated")), Ordering::Relaxed);

        let mut ui = Ui { args, shared: shared.clone(), hinst, windows: Vec::new(), render: None, failures: 0 };
        ui.build();
        if let Some(s) = ui.args.seconds {
            SetTimer(Some(hidden), TIMER_QUIT, (s * 1000.0).max(1.0) as u32, None);
        }

        let mut msg = MSG::default();
        while !QUIT_REQUESTED.load(Ordering::Relaxed) {
            let r = GetMessageW(&mut msg, None, 0, 0);
            if r.0 <= 0 {
                break;
            }
            if msg.message == WM_APP_RENDER_FAILED {
                ui.failures += 1;
                if ui.failures > 5 {
                    eprintln!("[ui] too many render failures, quitting");
                    break;
                }
            }
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
            if REBUILD_PENDING.swap(false, Ordering::Relaxed) && !QUIT_REQUESTED.load(Ordering::Relaxed) {
                ui.rebuild();
            }
        }

        // Sample memory while everything is still alive, then tear down.
        let mem = stats::memory_mb();
        ui.stop_render();
        if !ui.args.windowed {
            eprintln!("[tree] at exit:");
            attach::dump_tree(&ui.windows);
        }
        ui.destroy_windows();
        let _ = DestroyWindow(hidden);
        if let Some(path) = &ui.args.stats {
            let json = stats::to_json(&shared, mem);
            match std::fs::write(path, &json) {
                Ok(()) => eprintln!("[ui] stats -> {path}\n{json}"),
                Err(e) => eprintln!("[ui] failed to write stats: {e}"),
            }
        }
    }
}
