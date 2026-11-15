//! Render thread: one D3D11 device, one D2D device context, one flip-model swap chain per monitor.

use std::mem::ManuallyDrop;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Direct2D::Common::*;
use windows::Win32::Graphics::Direct2D::*;
use windows::Win32::Graphics::Direct3D::*;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::DirectWrite::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::*;
use windows::Win32::System::Com::*;
use windows::Win32::System::Threading::*;
use windows::core::{Interface, PCWSTR, Result, w};
use windows_numerics::{Matrix3x2, Vector2};

use crate::scene::{self, LINES};
use crate::{qpc_now, snapshot};

/// A window to render into. HWND is stored as isize so the struct is Send.
#[derive(Clone, Debug)]
pub struct Target {
    pub hwnd: isize,
    pub w: u32,
    pub h: u32,
    pub dpi: u32,
    pub hz: u32,
    pub mode: &'static str,
}

#[derive(Default)]
pub struct StatsAccum {
    pub adapter: String,
    pub monitors: Vec<Target>,
    pub cpu_ms: Vec<f64>,
    pub interval_ms: Vec<f64>,
    pub frames: u64,
    pub first_present_qpc: Option<i64>,
    pub last_present_qpc: i64,
    pub slow_frames: u64,
    pub startup_ms: Option<f64>,
}

pub struct Shared {
    pub qpc_origin: i64,
    pub qpc_freq: f64,
    pub time_offset: f64,
    pub fps_cap: Option<f64>,
    pub snapshot: Option<(String, f64)>,
    pub snapshot_done: AtomicBool,
    pub stats: Mutex<StatsAccum>,
}

impl Shared {
    pub fn secs_since_launch(&self, qpc: i64) -> f64 {
        (qpc - self.qpc_origin) as f64 / self.qpc_freq
    }
}

struct Gpu {
    d3d: ID3D11Device,
    ctx: ID3D11DeviceContext,
    dxgi_factory: IDXGIFactory2,
    dc: ID2D1DeviceContext,
    dwrite: IDWriteFactory2,
    fallback: IDWriteFontFallback,
    white: ID2D1SolidColorBrush,
    adapter: String,
}

struct WordRes {
    sharp: ID2D1Bitmap1,
    blurred: ID2D1Bitmap1,
    blur: ID2D1Effect,
    out: ID2D1Image, // blur -> opacity chain output
    opacity: ID2D1Effect,
    x: f32,
    y: f32,
    w: f32,
    h: f32,
    start: f64,
}

struct Surface {
    t: Target,
    swap: IDXGISwapChain2,
    waitable: HANDLE,
    bb: ID2D1Bitmap1,
    blobs: Vec<ID2D1RadialGradientBrush>,
    line_idx: usize,
    words: Vec<WordRes>,
    font_px: f32,
    scale: f32,
}

impl Drop for Surface {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.waitable);
        }
    }
}

fn color(rgb: u32, a: f32) -> D2D1_COLOR_F {
    D2D1_COLOR_F {
        r: ((rgb >> 16) & 0xFF) as f32 / 255.0,
        g: ((rgb >> 8) & 0xFF) as f32 / 255.0,
        b: (rgb & 0xFF) as f32 / 255.0,
        a,
    }
}

fn set_float(e: &ID2D1Effect, index: i32, v: f32) -> Result<()> {
    unsafe { e.SetValue(index as u32, D2D1_PROPERTY_TYPE_FLOAT, &v.to_le_bytes()) }
}

unsafe fn create_gpu() -> Result<Gpu> {
    unsafe {
        let mut device: Option<ID3D11Device> = None;
        let mut ctx: Option<ID3D11DeviceContext> = None;
        let levels = [D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0];
        D3D11CreateDevice(
            None,
            D3D_DRIVER_TYPE_HARDWARE,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            Some(&levels),
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            Some(&mut ctx),
        )?;
        let d3d = device.unwrap();
        let ctx = ctx.unwrap();
        let dxgi_device: IDXGIDevice = d3d.cast()?;
        let adapter = dxgi_device.GetAdapter()?;
        let desc = adapter.GetDesc()?;
        let n = desc.Description.iter().position(|&c| c == 0).unwrap_or(desc.Description.len());
        let adapter_name = String::from_utf16_lossy(&desc.Description[..n]);
        let dxgi_factory: IDXGIFactory2 = adapter.GetParent()?;

        let opts = D2D1_FACTORY_OPTIONS { debugLevel: D2D1_DEBUG_LEVEL_NONE };
        let d2d_factory: ID2D1Factory1 = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, Some(&opts))?;
        let d2d_device = d2d_factory.CreateDevice(&dxgi_device)?;
        let dc = d2d_device.CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE)?;
        dc.SetDpi(96.0, 96.0); // work in physical pixels
        dc.SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        let white = dc.CreateSolidColorBrush(&color(0xFFFFFF, 1.0), None)?;

        let dwrite: IDWriteFactory2 = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED)?;
        let fallback = dwrite.GetSystemFontFallback()?;

        // Report whether the requested family resolves (variable font named instance).
        let mut coll: Option<IDWriteFontCollection> = None;
        dwrite.GetSystemFontCollection(&mut coll, false)?;
        let (mut idx, mut exists) = (0u32, windows::core::BOOL(0));
        coll.unwrap().FindFamilyName(w!("Segoe UI Variable Display"), &mut idx, &mut exists)?;
        eprintln!("[render] adapter: {adapter_name}; 'Segoe UI Variable Display' in system collection: {}", exists.as_bool());

        Ok(Gpu { d3d, ctx, dxgi_factory, dc, dwrite, fallback, white, adapter: adapter_name })
    }
}

unsafe fn create_surface(gpu: &Gpu, t: &Target) -> Result<Surface> {
    unsafe {
        let hwnd = HWND(t.hwnd as *mut _);
        let desc = DXGI_SWAP_CHAIN_DESC1 {
            Width: t.w,
            Height: t.h,
            Format: DXGI_FORMAT_B8G8R8A8_UNORM,
            Stereo: false.into(),
            SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
            BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount: 2,
            Scaling: DXGI_SCALING_STRETCH,
            SwapEffect: DXGI_SWAP_EFFECT_FLIP_DISCARD,
            AlphaMode: DXGI_ALPHA_MODE_IGNORE,
            Flags: DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT.0 as u32,
        };
        let swap1 = gpu.dxgi_factory.CreateSwapChainForHwnd(&gpu.d3d, hwnd, &desc, None, None)?;
        let _ = gpu.dxgi_factory.MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER | DXGI_MWA_NO_WINDOW_CHANGES);
        let swap: IDXGISwapChain2 = swap1.cast()?;
        swap.SetMaximumFrameLatency(1)?;
        let waitable = swap.GetFrameLatencyWaitableObject();

        let surface: IDXGISurface = swap.GetBuffer(0)?;
        let props = D2D1_BITMAP_PROPERTIES1 {
            pixelFormat: D2D1_PIXEL_FORMAT { format: DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode: D2D1_ALPHA_MODE_IGNORE },
            dpiX: 96.0,
            dpiY: 96.0,
            bitmapOptions: D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            colorContext: ManuallyDrop::new(None),
        };
        let bb = gpu.dc.CreateBitmapFromDxgiSurface(&surface, Some(&props))?;

        let radius = 0.5 * t.w.max(t.h) as f32;
        let mut blobs = Vec::new();
        for &c in &scene::BLOBS {
            let stops = [
                D2D1_GRADIENT_STOP { position: 0.0, color: color(c, 0.85) },
                D2D1_GRADIENT_STOP { position: 1.0, color: color(c, 0.0) },
            ];
            // ID2D1DeviceContext's 6-arg overload shadows the base one; call through the base interface.
            let rt: &ID2D1RenderTarget = &gpu.dc;
            let coll = rt.CreateGradientStopCollection(&stops, D2D1_GAMMA_2_2, D2D1_EXTEND_MODE_CLAMP)?;
            let rp = D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES {
                center: Vector2 { X: 0.0, Y: 0.0 },
                gradientOriginOffset: Vector2 { X: 0.0, Y: 0.0 },
                radiusX: radius,
                radiusY: radius,
            };
            blobs.push(gpu.dc.CreateRadialGradientBrush(&rp, None, &coll)?);
        }
        Ok(Surface {
            t: t.clone(),
            swap,
            waitable,
            bb,
            blobs,
            line_idx: usize::MAX,
            words: Vec::new(),
            font_px: 0.075 * t.h as f32,
            scale: t.dpi as f32 / 96.0,
        })
    }
}

fn word_bitmap_props() -> D2D1_BITMAP_PROPERTIES1 {
    D2D1_BITMAP_PROPERTIES1 {
        pixelFormat: D2D1_PIXEL_FORMAT { format: DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode: D2D1_ALPHA_MODE_PREMULTIPLIED },
        dpiX: 96.0,
        dpiY: 96.0,
        bitmapOptions: D2D1_BITMAP_OPTIONS_TARGET,
        colorContext: ManuallyDrop::new(None),
    }
}

/// Lays out line `idx` for this surface and builds per-word cached bitmaps (sharp + pre-blurred)
/// and the per-word blur -> opacity effect chain used while a word animates.
unsafe fn build_line(gpu: &Gpu, s: &mut Surface, idx: usize, line_start: f64) -> Result<()> {
    unsafe {
        s.words.clear();
        let line = &LINES[idx];
        let (text, ranges) = scene::line_text(idx);
        let (w, h) = (s.t.w as f32, s.t.h as f32);
        let font = s.font_px;
        let format = gpu.dwrite.CreateTextFormat(
            w!("Segoe UI Variable Display"),
            None,
            DWRITE_FONT_WEIGHT_SEMI_BOLD,
            DWRITE_FONT_STYLE_NORMAL,
            DWRITE_FONT_STRETCH_NORMAL,
            font,
            w!("en-us"),
        )?;
        format.SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER)?;
        format.SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP)?;
        if line.rtl {
            format.SetReadingDirection(DWRITE_READING_DIRECTION_RIGHT_TO_LEFT)?;
        }
        let max_w = 0.8 * w;
        let layout = gpu.dwrite.CreateTextLayout(&text, &format, max_w, h)?;
        layout.cast::<IDWriteTextLayout2>()?.SetFontFallback(&gpu.fallback)?;

        let mut tm = DWRITE_TEXT_METRICS::default();
        layout.GetMetrics(&mut tm)?;
        let mut n_lines = 0u32;
        let _ = layout.GetLineMetrics(None, &mut n_lines);
        let mut lms = vec![DWRITE_LINE_METRICS::default(); n_lines as usize];
        layout.GetLineMetrics(Some(&mut lms), &mut n_lines)?;
        if n_lines > 2 {
            eprintln!("[render] warning: line {} wraps to {} lines (spec max 2)", idx + 1, n_lines);
        }
        let origin_x = 0.1 * w;
        let origin_y = ((h - tm.height) * 0.5).round();

        let sigma = scene::BLUR_SIGMA_100 * s.scale;
        let margin = (3.0 * sigma + 0.2 * font).ceil();
        let props = word_bitmap_props();
        let transparent = color(0, 0.0);

        for (k, &(start, len)) in ranges.iter().enumerate() {
            // Where the word sits inside the full (wrapped, bidi-reordered) line layout.
            let mut hits = [DWRITE_HIT_TEST_METRICS::default(); 8];
            let mut nh = 0u32;
            layout.HitTestTextRange(start, len, 0.0, 0.0, Some(&mut hits), &mut nh)?;
            let hits = &hits[..(nh as usize).min(8)];
            let left = hits.iter().map(|m| m.left).fold(f32::MAX, f32::min);
            let right = hits.iter().map(|m| m.left + m.width).fold(f32::MIN, f32::max);
            let top = hits.iter().map(|m| m.top).fold(f32::MAX, f32::min);
            let line_h = hits.iter().map(|m| m.top + m.height).fold(f32::MIN, f32::max) - top;
            // Baseline of the visual line the word is on.
            let mut acc = 0.0f32;
            let mut base = lms.first().map(|l| l.baseline).unwrap_or(font);
            for lm in &lms {
                if (acc - top).abs() < 0.5 {
                    base = lm.baseline;
                    break;
                }
                acc += lm.height;
            }
            let ww = (right - left).max(1.0);

            // The word on its own layout, baseline-aligned to the full line.
            let word_text = &text[start as usize..(start + len) as usize];
            let wl = gpu.dwrite.CreateTextLayout(word_text, &format, ww, line_h)?;
            wl.SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING)?;
            wl.SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP)?;
            wl.cast::<IDWriteTextLayout2>()?.SetFontFallback(&gpu.fallback)?;
            let mut wn = 0u32;
            let mut wlm = [DWRITE_LINE_METRICS::default(); 1];
            let _ = wl.GetLineMetrics(Some(&mut wlm), &mut wn);
            let wbase = wlm[0].baseline;

            let bw = (ww + 2.0 * margin).ceil();
            let bh = (line_h + 2.0 * margin).ceil();
            let size = D2D_SIZE_U { width: bw as u32, height: bh as u32 };
            let sharp = gpu.dc.CreateBitmap(size, None, 0, &props)?;
            gpu.dc.SetTarget(&sharp);
            gpu.dc.BeginDraw();
            gpu.dc.Clear(Some(&transparent));
            gpu.dc.DrawTextLayout(
                Vector2 { X: margin, Y: margin + base - wbase },
                &wl,
                &gpu.white,
                D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT,
            );
            gpu.dc.EndDraw(None, None)?;

            let blur = gpu.dc.CreateEffect(&CLSID_D2D1GaussianBlur)?;
            blur.SetInput(0, &sharp, true);
            set_float(&blur, D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION.0, sigma)?;
            let blurred = gpu.dc.CreateBitmap(size, None, 0, &props)?;
            gpu.dc.SetTarget(&blurred);
            gpu.dc.BeginDraw();
            gpu.dc.Clear(Some(&transparent));
            gpu.dc.DrawImage(&blur.GetOutput()?, None, None, D2D1_INTERPOLATION_MODE_LINEAR, D2D1_COMPOSITE_MODE_SOURCE_OVER);
            gpu.dc.EndDraw(None, None)?;

            let opacity = gpu.dc.CreateEffect(&CLSID_D2D1Opacity)?;
            opacity.SetInput(0, &blur.GetOutput()?, true);
            let out = opacity.GetOutput()?;

            s.words.push(WordRes {
                sharp,
                blurred,
                blur,
                out,
                opacity,
                x: (origin_x + left - margin).round(),
                y: (origin_y + top - margin).round(),
                w: bw,
                h: bh,
                start: line_start + k as f64 * scene::WORD_STEP,
            });
        }
        gpu.dc.SetTarget(None);
        s.line_idx = idx;
        Ok(())
    }
}

unsafe fn draw_surface(gpu: &Gpu, s: &mut Surface, t: f64) -> Result<()> {
    unsafe {
        let (idx, in_line) = scene::locate(t);
        if idx != s.line_idx || s.words.is_empty() {
            build_line(gpu, s, idx, t - in_line)?;
        }
        let dc = &gpu.dc;
        let (w, h) = (s.t.w as f32, s.t.h as f32);
        dc.SetTarget(&s.bb);
        dc.BeginDraw();
        dc.SetTransform(&Matrix3x2::identity());
        dc.Clear(Some(&color(scene::BASE, 1.0)));
        let full = D2D_RECT_F { left: 0.0, top: 0.0, right: w, bottom: h };
        for (i, b) in s.blobs.iter().enumerate() {
            let (px, py, fx, fy) = scene::BLOB_PATHS[i];
            let tau = std::f64::consts::TAU;
            let cx = w * (0.5 + 0.32 * (tau * t / px + fx).sin() as f32);
            let cy = h * (0.5 + 0.32 * (tau * t / py + fy).sin() as f32);
            b.SetCenter(Vector2 { X: cx, Y: cy });
            dc.FillRectangle(&full, b);
        }

        let sigma = scene::BLUR_SIGMA_100 * s.scale;
        let rise = scene::RISE_EM * s.font_px;
        for wr in &s.words {
            let p = (t - wr.start) / scene::REVEAL;
            if p <= 0.0 {
                let r = D2D_RECT_F { left: wr.x, top: wr.y + rise.round(), right: wr.x + wr.w, bottom: wr.y + rise.round() + wr.h };
                dc.DrawBitmap(&wr.blurred, Some(&r), scene::DIM_OPACITY, D2D1_INTERPOLATION_MODE_LINEAR, None, None);
            } else if p >= 1.0 {
                let r = D2D_RECT_F { left: wr.x, top: wr.y, right: wr.x + wr.w, bottom: wr.y + wr.h };
                dc.DrawBitmap(&wr.sharp, Some(&r), 1.0, D2D1_INTERPOLATION_MODE_LINEAR, None, None);
            } else {
                // Animating: live Gaussian blur + opacity every frame.
                let e = scene::ease_out_cubic(p) as f32;
                set_float(&wr.blur, D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION.0, sigma * (1.0 - e))?;
                set_float(&wr.opacity, D2D1_OPACITY_PROP_OPACITY.0, scene::DIM_OPACITY + (1.0 - scene::DIM_OPACITY) * e)?;
                let off = Vector2 { X: wr.x, Y: wr.y + rise * (1.0 - e) };
                dc.DrawImage(&wr.out, Some(&off), None, D2D1_INTERPOLATION_MODE_LINEAR, D2D1_COMPOSITE_MODE_SOURCE_OVER);
            }
        }
        dc.EndDraw(None, None)?;
        Ok(())
    }
}

unsafe fn read_back(gpu: &Gpu, s: &Surface) -> Result<(u32, u32, Vec<u8>)> {
    unsafe {
        let tex: ID3D11Texture2D = s.swap.GetBuffer(0)?;
        let mut desc = D3D11_TEXTURE2D_DESC::default();
        tex.GetDesc(&mut desc);
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
        desc.MiscFlags = 0;
        let mut staging: Option<ID3D11Texture2D> = None;
        gpu.d3d.CreateTexture2D(&desc, None, Some(&mut staging))?;
        let staging = staging.unwrap();
        gpu.ctx.CopyResource(&staging, &tex);
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        gpu.ctx.Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))?;
        let row = desc.Width as usize * 4;
        let mut out = vec![0u8; row * desc.Height as usize];
        for y in 0..desc.Height as usize {
            let src = std::slice::from_raw_parts((mapped.pData as *const u8).add(y * mapped.RowPitch as usize), row);
            out[y * row..(y + 1) * row].copy_from_slice(src);
        }
        gpu.ctx.Unmap(&staging, 0);
        for px in out.chunks_exact_mut(4) {
            px[3] = 255; // alpha-ignore swap chain
        }
        Ok((desc.Width, desc.Height, out))
    }
}

/// Runs until `stop` is set. Returns Err on device loss etc. (the UI thread then rebuilds).
pub fn run(targets: Vec<Target>, shared: Arc<Shared>, stop: Arc<AtomicBool>) -> Result<()> {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        let r = run_inner(&targets, &shared, &stop);
        CoUninitialize();
        r
    }
}

unsafe fn run_inner(targets: &[Target], shared: &Shared, stop: &AtomicBool) -> Result<()> {
    unsafe {
        let gpu = create_gpu()?;
        let mut surfaces = Vec::new();
        for t in targets {
            surfaces.push(create_surface(&gpu, t)?);
        }
        {
            let mut st = shared.stats.lock().unwrap();
            st.adapter = gpu.adapter.clone();
            st.monitors = targets.to_vec();
        }
        let handles: Vec<HANDLE> = surfaces.iter().map(|s| s.waitable).collect();
        let hz = targets.first().map(|t| t.hz).unwrap_or(60).max(1) as f64;
        let target_hz = shared.fps_cap.map(|c| c.min(hz)).unwrap_or(hz);
        let slow_ms = 1.5 * 1000.0 / target_hz;

        // High-resolution timer for --fps capping.
        let timer = match shared.fps_cap {
            Some(_) => Some(CreateWaitableTimerExW(None, PCWSTR::null(), CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS.0)?),
            None => None,
        };
        let cap_period = shared.fps_cap.map(|c| (shared.qpc_freq / c) as i64).unwrap_or(0);
        let mut next_deadline = qpc_now();

        let mut last_present: Option<i64> = None;
        let mut skip_interval = false;
        let mut local_cpu = Vec::with_capacity(1 << 16);
        let mut local_int = Vec::with_capacity(1 << 16);
        let mut local_frames = 0u64;
        let mut local_slow = 0u64;
        let mut non_ok = 0u64;

        let result = (|| -> Result<()> {
            while !stop.load(Ordering::Relaxed) {
                let wr = WaitForMultipleObjects(&handles, true, 500);
                if wr == WAIT_TIMEOUT {
                    continue;
                }
                if let Some(tm) = timer {
                    let now = qpc_now();
                    if next_deadline > now {
                        let due = -(((next_deadline - now) as f64 / shared.qpc_freq * 1e7) as i64).max(1);
                        SetWaitableTimer(tm, &due, 0, None, None, false)?;
                        WaitForSingleObject(tm, 1000);
                        next_deadline += cap_period;
                    } else {
                        next_deadline = now + cap_period;
                    }
                }
                let t0 = qpc_now();
                let t = shared.secs_since_launch(t0) + shared.time_offset;
                let mut excluded = false;
                for (i, s) in surfaces.iter_mut().enumerate() {
                    draw_surface(&gpu, s, t)?;
                    if i == 0 {
                        if let Some((path, after)) = &shared.snapshot {
                            if !shared.snapshot_done.load(Ordering::Relaxed) && shared.secs_since_launch(t0) >= *after {
                                let (w, h, px) = read_back(&gpu, s)?;
                                match snapshot::save_png(path, w, h, &px) {
                                    Ok(()) => eprintln!("[render] snapshot {w}x{h} at t={t:.3}s (line {}) -> {path}", s.line_idx + 1),
                                    Err(e) => eprintln!("[render] snapshot failed: {e}"),
                                }
                                shared.snapshot_done.store(true, Ordering::Relaxed);
                                excluded = true;
                            }
                        }
                    }
                    let hr = s.swap.Present(1, DXGI_PRESENT(0));
                    hr.ok()?;
                    if hr != S_OK {
                        // Success codes such as DXGI_STATUS_OCCLUDED are not errors; count and log them.
                        if non_ok == 0 {
                            eprintln!("[render] first non-S_OK Present result: 0x{:08x} (monitor {i})", hr.0);
                        }
                        non_ok += 1;
                    }
                }
                let t1 = qpc_now();
                local_frames += 1;
                if !excluded {
                    local_cpu.push((t1 - t0) as f64 * 1000.0 / shared.qpc_freq);
                }
                if let Some(lp) = last_present {
                    if !skip_interval {
                        let iv = (t1 - lp) as f64 * 1000.0 / shared.qpc_freq;
                        if iv > slow_ms {
                            local_slow += 1;
                        }
                        local_int.push(iv);
                    }
                }
                skip_interval = excluded;
                last_present = Some(t1);
                if local_frames == 1 {
                    let mut st = shared.stats.lock().unwrap();
                    if st.first_present_qpc.is_none() {
                        st.first_present_qpc = Some(t1);
                        st.startup_ms = Some(crate::ms_since_process_creation());
                        eprintln!("[render] first present; startup {:.1} ms", st.startup_ms.unwrap());
                    }
                }
                // Flush to shared stats once in a while (cheap; keeps memory bounded per run).
                if local_frames % 256 == 0 {
                    flush(shared, &mut local_cpu, &mut local_int, &mut local_frames, &mut local_slow, t1);
                }
            }
            Ok(())
        })();
        if non_ok > 0 {
            eprintln!("[render] non-S_OK Present results: {non_ok}");
        }
        if let Some(lp) = last_present {
            flush(shared, &mut local_cpu, &mut local_int, &mut local_frames, &mut local_slow, lp);
        }
        if let Some(tm) = timer {
            let _ = CloseHandle(tm);
        }
        gpu.dc.SetTarget(None);
        result
    }
}

fn flush(shared: &Shared, cpu: &mut Vec<f64>, int: &mut Vec<f64>, frames: &mut u64, slow: &mut u64, last: i64) {
    let mut st = shared.stats.lock().unwrap();
    st.cpu_ms.append(cpu);
    st.interval_ms.append(int);
    st.frames += *frames;
    st.slow_frames += *slow;
    st.last_present_qpc = last;
    *frames = 0;
    *slow = 0;
}
