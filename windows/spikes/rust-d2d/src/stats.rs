//! Stats JSON exactly per the spec.

use windows::Win32::System::ProcessStatus::{GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS, PROCESS_MEMORY_COUNTERS_EX};
use windows::Win32::System::Threading::GetCurrentProcess;

use crate::render::Shared;

/// (working set MB, private bytes MB)
pub fn memory_mb() -> (f64, f64) {
    let mut pmc = PROCESS_MEMORY_COUNTERS_EX::default();
    unsafe {
        let _ = GetProcessMemoryInfo(
            GetCurrentProcess(),
            &mut pmc as *mut _ as *mut PROCESS_MEMORY_COUNTERS,
            std::mem::size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32,
        );
    }
    let mb = |b: usize| b as f64 / (1024.0 * 1024.0);
    (mb(pmc.WorkingSetSize), mb(pmc.PrivateUsage))
}

fn pct(sorted: &[f64], p: f64) -> f64 {
    if sorted.is_empty() {
        return 0.0;
    }
    let rank = ((p / 100.0) * sorted.len() as f64).ceil() as usize;
    sorted[rank.clamp(1, sorted.len()) - 1]
}

fn summary(v: &[f64]) -> String {
    let mut s = v.to_vec();
    s.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let avg = if s.is_empty() { 0.0 } else { s.iter().sum::<f64>() / s.len() as f64 };
    format!(
        "{{\"avg\":{:.3},\"p50\":{:.3},\"p95\":{:.3},\"p99\":{:.3}}}",
        avg,
        pct(&s, 50.0),
        pct(&s, 95.0),
        pct(&s, 99.0)
    )
}

fn esc(s: &str) -> String {
    let mut o = String::new();
    for c in s.chars() {
        match c {
            '"' => o.push_str("\\\""),
            '\\' => o.push_str("\\\\"),
            c if (c as u32) < 0x20 => o.push_str(&format!("\\u{:04x}", c as u32)),
            c => o.push(c),
        }
    }
    o
}

pub fn to_json(shared: &Shared, (ws, private): (f64, f64)) -> String {
    let st = shared.stats.lock().unwrap();
    let seconds = match st.first_present_qpc {
        Some(f) => (st.last_present_qpc - f) as f64 / shared.qpc_freq,
        None => 0.0,
    };
    let fps = if seconds > 0.0 { st.frames.saturating_sub(1) as f64 / seconds } else { 0.0 };
    let monitors: Vec<String> = st
        .monitors
        .iter()
        .map(|m| format!("{{\"w\":{},\"h\":{},\"dpi\":{},\"hz\":{},\"mode\":\"{}\"}}", m.w, m.h, m.dpi, m.hz, m.mode))
        .collect();
    let (created, kernel, user) = crate::process_times();
    let _ = created;
    let wall_ms = crate::ms_since_process_creation();
    let cores = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1) as f64;
    let cpu_ms = (kernel + user) as f64 / 10_000.0;
    let cpu_pct = if wall_ms > 0.0 { cpu_ms / wall_ms / cores * 100.0 } else { 0.0 };
    format!(
        "{{ \"stack\": \"rust-d2d\", \"adapter\": \"{}\", \"monitors\": [{}],\n  \"seconds\": {:.3}, \"frames\": {}, \"fps\": {:.2},\n  \"cpuMsPerFrame\": {},\n  \"frameIntervalMs\": {}, \"slowFrames\": {},\n  \"startupMs\": {:.1}, \"workingSetMB\": {:.1}, \"privateMB\": {:.1}, \"processCpuPercent\": {:.2} }}\n",
        esc(&st.adapter),
        monitors.join(","),
        seconds,
        st.frames,
        fps,
        summary(&st.cpu_ms),
        summary(&st.interval_ms),
        st.slow_frames,
        st.startup_ms.unwrap_or(0.0),
        ws,
        private,
        cpu_pct
    )
}
