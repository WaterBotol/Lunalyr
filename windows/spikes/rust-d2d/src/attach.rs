//! Monitor enumeration and attaching behind the desktop icons (24H2 Progman child / legacy WorkerW).

use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::UI::HiDpi::*;
use windows::Win32::UI::WindowsAndMessaging::*;
use windows::core::{BOOL, Error, PCWSTR, Result, w};

use crate::render::Target;

const WALL_CLASS: PCWSTR = w!("LunalyrSpikeRsWallpaper");
const WIN_CLASS: PCWSTR = w!("LunalyrSpikeRsWindowed");

extern "system" fn wall_proc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    unsafe {
        match msg {
            WM_ERASEBKGND => LRESULT(1),
            WM_PAINT => {
                let mut ps = PAINTSTRUCT::default();
                BeginPaint(hwnd, &mut ps);
                let _ = EndPaint(hwnd, &ps);
                LRESULT(0)
            }
            WM_NCHITTEST => LRESULT(HTTRANSPARENT as isize),
            WM_MOUSEACTIVATE => LRESULT(MA_NOACTIVATE as isize),
            _ => DefWindowProcW(hwnd, msg, wp, lp),
        }
    }
}

extern "system" fn windowed_proc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    unsafe {
        match msg {
            WM_ERASEBKGND => LRESULT(1),
            WM_PAINT => {
                let mut ps = PAINTSTRUCT::default();
                BeginPaint(hwnd, &mut ps);
                let _ = EndPaint(hwnd, &ps);
                LRESULT(0)
            }
            WM_DPICHANGED => {
                // Keep the 1280x720 physical client size; rebuild for the new DPI.
                crate::schedule_rebuild(250);
                LRESULT(0)
            }
            WM_CLOSE => {
                PostQuitMessage(0);
                LRESULT(0)
            }
            _ => DefWindowProcW(hwnd, msg, wp, lp),
        }
    }
}

pub fn register_class(hinst: HINSTANCE) {
    unsafe {
        let cursor = LoadCursorW(None, IDC_ARROW).unwrap_or_default();
        for (name, proc_) in [(WALL_CLASS, wall_proc as _), (WIN_CLASS, windowed_proc as _)] {
            let wc = WNDCLASSEXW {
                cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
                lpfnWndProc: Some(proc_),
                hInstance: hinst,
                hCursor: cursor,
                lpszClassName: name,
                ..Default::default()
            };
            RegisterClassExW(&wc);
        }
    }
}

#[derive(Clone, Debug)]
pub struct MonitorInfo {
    pub rect: RECT,
    pub dpi: u32,
    pub hz: u32,
    pub primary: bool,
    pub device: String,
}

fn monitor_details(hmon: HMONITOR) -> MonitorInfo {
    unsafe {
        let mut mi = MONITORINFOEXW::default();
        mi.monitorInfo.cbSize = std::mem::size_of::<MONITORINFOEXW>() as u32;
        let _ = GetMonitorInfoW(hmon, &mut mi.monitorInfo);
        let (mut dx, mut dy) = (96u32, 96u32);
        let _ = GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, &mut dx, &mut dy);
        let mut dm = DEVMODEW { dmSize: std::mem::size_of::<DEVMODEW>() as u16, ..Default::default() };
        let _ = EnumDisplaySettingsW(PCWSTR(mi.szDevice.as_ptr()), ENUM_CURRENT_SETTINGS, &mut dm);
        let n = mi.szDevice.iter().position(|&c| c == 0).unwrap_or(mi.szDevice.len());
        MonitorInfo {
            rect: mi.monitorInfo.rcMonitor,
            dpi: dx,
            hz: dm.dmDisplayFrequency.max(1),
            primary: (mi.monitorInfo.dwFlags & MONITORINFOF_PRIMARY) != 0,
            device: String::from_utf16_lossy(&mi.szDevice[..n]),
        }
    }
}

extern "system" fn enum_mon(h: HMONITOR, _: HDC, _: *mut RECT, lp: LPARAM) -> BOOL {
    let v = unsafe { &mut *(lp.0 as *mut Vec<HMONITOR>) };
    v.push(h);
    true.into()
}

pub fn monitors() -> Vec<MonitorInfo> {
    let mut hs: Vec<HMONITOR> = Vec::new();
    unsafe {
        let _ = EnumDisplayMonitors(None, None, Some(enum_mon), LPARAM(&mut hs as *mut _ as isize));
    }
    let mut v: Vec<MonitorInfo> = hs.into_iter().map(monitor_details).collect();
    v.sort_by_key(|m| !m.primary); // primary first (it is "the first monitor" for --snapshot)
    v
}

pub fn create_windowed(hinst: HINSTANCE) -> Result<(HWND, Target)> {
    unsafe {
        let mon = monitors().into_iter().next().ok_or_else(|| Error::from(E_FAIL))?;
        let style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        let mut r = RECT { left: 0, top: 0, right: 1280, bottom: 720 };
        AdjustWindowRectExForDpi(&mut r, style, false, WINDOW_EX_STYLE(0), mon.dpi)?;
        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            WIN_CLASS,
            w!("Lunalyr spike (rust-d2d)"),
            style,
            mon.rect.left + 80,
            mon.rect.top + 80,
            r.right - r.left,
            r.bottom - r.top,
            None,
            None,
            Some(hinst),
            None,
        )?;
        let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        let mut cr = RECT::default();
        GetClientRect(hwnd, &mut cr)?;
        let dpi = GetDpiForWindow(hwnd);
        let hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
        let hz = monitor_details(hmon).hz;
        Ok((
            hwnd,
            Target {
                hwnd: hwnd.0 as isize,
                w: (cr.right - cr.left) as u32,
                h: (cr.bottom - cr.top) as u32,
                dpi,
                hz,
                mode: "windowed",
            },
        ))
    }
}

fn find_child(parent: HWND, class: PCWSTR) -> Option<HWND> {
    unsafe { FindWindowExW(Some(parent), None, class, PCWSTR::null()).ok().filter(|h| !h.is_invalid()) }
}

struct LegacySearch {
    workerw: Option<HWND>,
}

extern "system" fn enum_top(h: HWND, lp: LPARAM) -> BOOL {
    unsafe {
        let s = &mut *(lp.0 as *mut LegacySearch);
        if find_child(h, w!("SHELLDLL_DefView")).is_some() {
            // The WorkerW that follows the one hosting the icons is the wallpaper layer.
            if let Ok(ww) = FindWindowExW(None, Some(h), w!("WorkerW"), PCWSTR::null()) {
                if !ww.is_invalid() {
                    s.workerw = Some(ww);
                }
            }
        }
        true.into()
    }
}

/// Creates one wallpaper window per monitor behind the desktop icons.
pub fn attach_all(hinst: HINSTANCE, layered: bool, force_legacy: bool) -> Result<Vec<(HWND, Target)>> {
    unsafe {
        let progman = FindWindowW(w!("Progman"), PCWSTR::null())?;
        let child_workerw = find_child(progman, w!("WorkerW"));
        let defview = find_child(progman, w!("SHELLDLL_DefView"));
        let (parent, insert_after, mode) = if child_workerw.is_some() && !force_legacy {
            eprintln!("[attach] 24H2 layout: Progman {:?} has child WorkerW {:?}, DefView {:?}", progman.0, child_workerw.unwrap().0, defview.map(|d| d.0));
            (progman, defview, "24h2")
        } else {
            let mut res = 0usize;
            let _ = SendMessageTimeoutW(progman, 0x052C, WPARAM(0xD), LPARAM(0x1), SMTO_NORMAL, 1000, Some(&mut res));
            let mut s = LegacySearch { workerw: None };
            let _ = EnumWindows(Some(enum_top), LPARAM(&mut s as *mut _ as isize));
            let ww = s.workerw.ok_or_else(|| Error::new(E_FAIL, "legacy: wallpaper WorkerW not found"))?;
            eprintln!("[attach] legacy layout: parenting to WorkerW {:?}", ww.0);
            (ww, None, "legacy")
        };

        let vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        let vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        let mut out = Vec::new();
        for m in monitors() {
            // Spec: Progman's client coordinates span the virtual screen -> offset by its origin.
            let x = m.rect.left - vx;
            let y = m.rect.top - vy;
            let mut pt = POINT { x: m.rect.left, y: m.rect.top };
            let _ = ScreenToClient(parent, &mut pt);
            if pt.x != x || pt.y != y {
                eprintln!("[attach] note: ScreenToClient gives ({},{}) vs virtual-screen offset ({x},{y})", pt.x, pt.y);
            }
            let w = m.rect.right - m.rect.left;
            let h = m.rect.bottom - m.rect.top;
            let ex = WS_EX_NOACTIVATE | if layered { WS_EX_LAYERED } else { WINDOW_EX_STYLE(0) };
            let hwnd = CreateWindowExW(
                ex,
                WALL_CLASS,
                w!("Lunalyr spike (rust-d2d) wallpaper"),
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_DISABLED,
                x,
                y,
                w,
                h,
                Some(parent),
                None,
                Some(hinst),
                None,
            )?;
            if layered {
                SetLayeredWindowAttributes(hwnd, COLORREF(0), 255, LWA_ALPHA)?;
            }
            if let Some(dv) = insert_after {
                // Directly below SHELLDLL_DefView, i.e. above the wallpaper WorkerW.
                SetWindowPos(hwnd, Some(dv), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)?;
            }
            eprintln!("[attach] {} {}x{} at ({x},{y}) dpi {} {} Hz -> hwnd {:?}", m.device, w, h, m.dpi, m.hz, hwnd.0);
            out.push((hwnd, Target { hwnd: hwnd.0 as isize, w: w as u32, h: h as u32, dpi: m.dpi, hz: m.hz, mode }));
        }
        Ok(out)
    }
}

fn class_name(h: HWND) -> String {
    let mut buf = [0u16; 128];
    let n = unsafe { GetClassNameW(h, &mut buf) };
    String::from_utf16_lossy(&buf[..n.max(0) as usize])
}

/// Prints the parent's direct children in z-order (top first) with rect / visibility.
pub fn dump_tree(ours: &[HWND]) {
    unsafe {
        let Some(&first) = ours.first() else { return };
        let Ok(parent) = GetParent(first) else { return };
        let pex = GetWindowLongPtrW(parent, GWL_EXSTYLE);
        let mut pr = RECT::default();
        let _ = GetWindowRect(parent, &mut pr);
        eprintln!(
            "[tree] parent {:?} class={} exstyle=0x{:08x} (NOREDIRECTIONBITMAP={}) rect=({},{},{},{})",
            parent.0,
            class_name(parent),
            pex,
            (pex as u32 & WS_EX_NOREDIRECTIONBITMAP.0) != 0,
            pr.left,
            pr.top,
            pr.right,
            pr.bottom
        );
        let mut h = GetWindow(parent, GW_CHILD).ok();
        let mut z = 0;
        while let Some(c) = h.filter(|h| !h.is_invalid()) {
            let mut r = RECT::default();
            let _ = GetWindowRect(c, &mut r);
            let ex = GetWindowLongPtrW(c, GWL_EXSTYLE) as u32;
            let st = GetWindowLongPtrW(c, GWL_STYLE) as u32;
            eprintln!(
                "[tree]  z={z} hwnd={:?} class={}{} visible={} style=0x{st:08x} exstyle=0x{ex:08x} rect=({},{},{},{})",
                c.0,
                class_name(c),
                if ours.contains(&c) { " (ours)" } else { "" },
                IsWindowVisible(c).as_bool(),
                r.left,
                r.top,
                r.right,
                r.bottom
            );
            z += 1;
            h = GetWindow(c, GW_HWNDNEXT).ok();
        }
    }
}
