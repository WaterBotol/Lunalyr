# Attribution and third-party components

This is an unofficial modification of AkkiCode06/verso, pinned in UPSTREAM.json. Its original GPLv3 plus Commons Clause licence is retained unchanged in LICENSE and bundled as Verso-LICENSE.txt.

The updater uses [Sparkle 2.10.0](https://github.com/sparkle-project/Sparkle/releases/tag/2.10.0), resolved by Swift Package Manager. Sparkle is distributed under the MIT licence and includes components under their own licences. The complete licence file from that exact tag is bundled as Sources/Resources/Sparkle-LICENSE.txt. See [the upstream licence](https://github.com/sparkle-project/Sparkle/blob/2.10.0/LICENSE).

The dedicated native visualisers reuse [Synesthia](https://github.com/jonjaques/synesthia), copyright (c) 2026 Jon Jaques, under the MIT licence. The pinned commit is 05144bf0f03eea5cc4af8135e9d71553f1ac9202. Its audio analyser, uniform layout, Aurora/Spectrum Tunnel shaders and GPU resolution helpers are adapted for Lunalyr. The full notice is bundled as Sources/Resources/Synesthia-LICENSE.txt; [the adaptation manifest](ThirdParty/Synesthia/SOURCE.md) records source files and local changes. No Synesthia branding, icons or demo music is included.

No source code from VoxelWall, VoxelVerse or Lyrimuse has been incorporated. The reference clip is not distributed. The local Lyrimuse bridge reads exported file formats; it does not bundle those services or their lyric databases.

Fosfora Aurora adapts [Fosfora](https://github.com/kevinraymond/fosfora)'s Aurora shader, copyright (c) 2025-2026 Kevin Raymond, under its MIT licence option. Pinned commit: 2e85d3babca5b7f27426e211e58fe3c831fb667f. The original WGSL, descriptor and [adaptation manifest](ThirdParty/Fosfora/SOURCE.md) are retained. The complete notice is bundled as Sources/Resources/Fosfora-LICENSE.txt. No Fosfora engine, branding, demo footage or audio is included.

## Yams

Yams 6.2.2 by JP Sim and contributors (MIT) parses LRCLIB's Lyricsfile YAML, including word timestamps. The package includes libyaml under MIT. Complete notices are bundled in Resources/Yams-LICENSE.txt and Resources/libyaml-LICENSE.txt.

## Wavefield

Wavefield and Wavefield Sphere adapt [Wavefield](https://github.com/niko-dellic/wavefield)'s Default preset, modal audio mapping and field shader, copyright (c) 2026 Niko Dellic, under MIT. Pinned commit: 3858a657190ebd09bee704caa094e7e1fd3c6cc2. The [adaptation manifest](ThirdParty/Wavefield/SOURCE.md) records the specific sources and local changes. The full notice is bundled as Sources/Resources/Wavefield-LICENSE.txt.

Wavefield Original bundles the pinned upstream WebGL engine and stock presets. Its Three.js (MIT) and postprocessing (zlib) dependency notices are bundled as Sources/Resources/Wavefield-Web-Dependencies-LICENSE.txt.

## Wavecore

Wavecore bundles Dinesh Kadavakuduru's original WebGL scene and audio mapping under MIT. Its MIT notice, the Ashima Arts and Stefan Gustavson simplex notice, and the WebGL dependency notices are bundled. See [Wavecore source and licences](ThirdParty/Wavecore/SOURCE.md).

## Cymatic Light 3D

Light 3D bundles Corey Epstein's MIT-licensed Cymatic preset and its renderer. See the [source and licence](ThirdParty/Cymatic/SOURCE.md).
