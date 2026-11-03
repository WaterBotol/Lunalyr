# Attribution and third-party components

This is an unofficial modification of AkkiCode06/verso, pinned in UPSTREAM.json. Its original GPLv3 plus Commons Clause licence is retained unchanged in LICENSE and bundled as Verso-LICENSE.txt.

The updater uses [Sparkle 2.10.0](https://github.com/sparkle-project/Sparkle/releases/tag/2.10.0), resolved by Swift Package Manager. Sparkle is distributed under the MIT licence and includes components under their own licences. The complete licence file from that exact tag is bundled as Sources/Resources/Sparkle-LICENSE.txt. See [the upstream licence](https://github.com/sparkle-project/Sparkle/blob/2.10.0/LICENSE).

The dedicated native visualisers reuse [Synesthia](https://github.com/jonjaques/synesthia), copyright (c) 2026 Jon Jaques, under the MIT licence. The pinned commit is 05144bf0f03eea5cc4af8135e9d71553f1ac9202. Its audio analyser, uniform layout, Aurora/Spectrum Tunnel shaders and GPU resolution helpers are adapted for Lunalyr. The full notice is bundled as Sources/Resources/Synesthia-LICENSE.txt; [the adaptation manifest](ThirdParty/Synesthia/SOURCE.md) records source files and local changes. No Synesthia branding, icons or demo music is included.

No source code from VoxelWall, VoxelVerse or Lyrimuse has been incorporated. The reference clip is not distributed. The local Lyrimuse bridge reads exported file formats; it does not bundle those services or their lyric databases.
