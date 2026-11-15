# thinking-orbs

- Upstream: https://github.com/Jakubantalik/thinking-orbs
- Commit: de85557ca220332586d070d8788c0e1d6e877a0d (npm `thinking-orbs` 0.3.1, spec 1.0.0)
- Licence: MIT, Copyright (c) 2026 Jakub Antalik (`LICENSE` here, and in the header of the port).
- Ported to C# in `src/Lunalyr.Core/Motion/ThinkingOrbs/ThinkingOrbEngine.cs` (engine and presets), matching the Mac's
  `ThinkingOrbEngine.swift` (next/tester-fixes 64eee43).
- `orbs-golden.json` and `orbs-spec.json` are upstream's `spec/` files, unchanged: the golden frames are the C# port's
  unit-test fixture (`tests/Lunalyr.Core.Tests/Motion/ThinkingOrbEngineTests.cs`).
