# thinking-orbs

Source: https://github.com/Jakubantalik/thinking-orbs
Pinned commit: de85557ca220332586d070d8788c0e1d6e877a0d (library 0.3.1, spec 1.0.0)
Copyright (c) 2026 Jakub Antalik. MIT licence; the complete notice is bundled as Sources/Resources/ThinkingOrbs-LICENSE.txt.

The dot engine is transcribed to Swift in Sources/UI/ThinkingOrbEngine.swift: `src/engine/core.ts` (projection, hash, value noise, Fibonacci lattice, cull/clamp/z-sort), the nine mode painters in `orbits.ts`, `lattice.ts` (globe, rubik, wave), `braid.ts`, `ribbon.ts` (ribbon and ring), `morph.ts` and `web.ts`, the base profiles and scaling rules in `profiles.ts`, and the nine-state by two-size presets in `src/presets.ts`. The library's own `spec/orbs-spec.json` and `spec/orbs-golden.json` are kept here unchanged; Tests/Swift/ThinkingOrbEngineTests.swift checks the Swift engine against every golden case (9 states x 2 sizes x 4 times) to the library's 1e-4 tolerance.

Local adaptations, all outside the geometry: the `listening` wave takes an optional `swell` option (default 1, upstream's depth) that Lunalyr raises with the music; dots are coloured from the lyric palette instead of grey (Sources/UI/ThinkingOrb.swift); the countdown ring, the audio mapping, the focus into `breathing` and the halo are Lunalyr's own. No React component, demo, theme detection or build tooling is included.
