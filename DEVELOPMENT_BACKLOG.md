# Development continuation

The user asked to finish the current visualiser/lyric-size/custom-wallpaper batch, then wait one hour before resuming earlier postponed work. Do not start the earlier backlog before that delay. Preserve new feedback received in the meantime.

## Current batch: 0.4.8 / build 15

- Add Aurora Curtains, a dedicated visualiser based on the ambient GPU Aurora.
- Correct visualiser colour richness and the full sensitivity range, remove irrelevant controls, and avoid hard saturation of ribbon energy.
- Keep lyric user scaling after neutral fitting so the fit cannot undo the size control. Fix Editorial face selection changing when rolling windows shift.
- Bound stalled capture startup, show the startup stage, audio meters and packet count. Capture follows the actual active wallpaper instead of a blanket battery restriction.
- Support imported local images and silent looping videos, retain app-managed copies, and handle video orientation.
- Live capture still requires permission for the actual app copy. The diagnostic build reported a macOS TCC denial and zero packets on compatibility capture. Do not describe a GPU fixture render as successful live system capture.

## Earlier work to audit after the requested delay

1. Read new user bugchecking feedback, Git status and current Actions results first. Preserve existing work and preferences.
2. The user explicitly chose “Leave capture permissions for later.” Do not prompt again, change privacy permissions, or enable capture to work around that choice. Finish actual Mac capture and response verification only when the user makes permission available: real audio packets, silence/playback response, route changes, wake and cancellation. Never bypass a denied privacy permission.
3. Profile Release CPU/GPU with a controlled song/style/frame-rate/display comparison. Record this Mac's hardware; it cannot establish improvement on the older Mac that previously showed about 154% CPU.
4. Audit expressive lyric entrances, held words and exits; clipping/horizon artefacts and Constellation line stability; instrumental interludes.
5. Verify per-style faint next-word previews, romanisation and physical multi-monitor behaviour where the necessary hardware is available.
6. Audit remaining non-working controls and UI regressions against actual running behaviour.
7. Revisit the Apple Music investigation and the user's latest scope before implementation. The reference is Dynamic-Lyrics, App Store ID 6476125287, not jackson-storm/DynamicNotch. Its AM-source preference is documented, but its retrieval implementation is unverified. A separate MIT project reads local Music cache TTML. Do not infer a method from an icon or claim an official public lyric-content API.
8. Lock-screen integration was posed as a question. Research and answer it; do not silently treat every question-marked item as a feature request.

Keep the upstream licence and attribution intact. App identity remains local.verso.custom for preferences/updater compatibility. Stable feed publication is separate from manual preview downloads; 0.4.5 is the approved stable release. Keep private signing material out of Git, logs and chat.
