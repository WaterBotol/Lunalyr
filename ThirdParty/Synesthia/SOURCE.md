# Synesthia visualisers

Source: https://github.com/jonjaques/synesthia
Pinned commit: 05144bf0f03eea5cc4af8135e9d71553f1ac9202
Copyright (c) 2026 Jon Jaques. MIT licence, bundled in Sources/Resources/Synesthia-LICENSE.txt.

Reused source: the audio analyser, VizUniforms definition, Aurora and Spectrum Tunnel shaders, and GPU resolution ladder/timer. The render host is adapted to Lunalyr’s scheduled wallpaper frames, existing capture, preferences and desktop windows. No Synesthia icon, wordmark, name branding, recordings or demo music is bundled.

Local changes: Swift 5 compatibility; native PCM decoder input; FFT setup cleanup; bounded sample-rate input; no kick-driven tunnel camera jumps; aurora curvature uses a smoothed spectrum instead of unstable waveform phase; gentler transient scaling and desktop defaults. The Silk Ribbons variation uses the same aurora renderer with restrained settings.

The imported shader comments retain their procedural-art references. Everything is rendered locally, with no browser or remote assets.
