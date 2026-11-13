import SwiftUI

// MARK: - The desktop player's tokens
//
// The player lives on the wallpaper, always dark (white type on the album's
// deep shade), so it has its own small set of tokens beside the window ones in
// `DesignTokens.swift`. docs/DESIGN.md ("Desktop player") is the spec; every
// value the player draws with comes from here, never a number of its own.
//
//     Text(artist).font(LunalyrDesign.Player.TypeScale.artist)
//         .foregroundStyle(LunalyrDesign.Player.Ink.secondary.color(look))

extension LunalyrDesign {
    enum Player {
        // MARK: Ink (text and glyphs on the card)

        /// Four levels of white, and nothing in between. Each passes over the
        /// card's darkest guarded album shade (`ArtworkPalette.deepContrast`
        /// under the card's black veil): primary and secondary 7:1 and more,
        /// tertiary 4.5:1 and more, quaternary is for disabled and decoration only.
        enum Ink {
            case primary, secondary, tertiary, quaternary

            /// White's opacity, normal and under Increase Contrast.
            func opacity(increaseContrast: Bool) -> Double {
                switch self {
                case .primary: return 1
                case .secondary: return increaseContrast ? 0.94 : 0.84
                case .tertiary: return increaseContrast ? 0.88 : 0.72
                case .quaternary: return increaseContrast ? 0.6 : 0.4
                }
            }

            func color(_ look: PlayerLook) -> Color {
                .white.opacity(opacity(increaseContrast: look.increaseContrast))
            }
        }

        // MARK: Fills and lines

        enum Fill {
            /// Under the pointer: an icon button's halo, a row.
            static let hover: Double = 0.08
            /// Selected or pressed: the lit row, an open pane's button.
            static let selected: Double = 0.16
            /// A recessed well: the search field, the player switch's track.
            static let well: Double = 0.24
            /// The card's veil over the material, so white type always reads.
            static let veil: Double = 0.24
            /// The album tint over the veil (`ArtworkPalette.cardTint`).
            static let tint: Double = 0.92
            /// A placeholder tile while a cover loads.
            static let placeholder: Double = 0.1
        }

        enum Line {
            /// Edges are drawn only under Increase Contrast: the card, capsules
            /// and fields are separated by fill and shadow, not by borders.
            static func hairline(_ look: PlayerLook) -> Color {
                look.increaseContrast ? .white.opacity(0.55) : .clear
            }
            /// Keyboard focus (the search field holding the keyboard).
            static func focus(_ look: PlayerLook) -> Color {
                .white.opacity(look.increaseContrast ? 1 : 0.7)
            }
            static let width: CGFloat = 1
            static let focusWidth: CGFloat = 1.5
        }

        // MARK: Type

        /// SF Pro at three weights only: regular, medium, semibold. Sentence
        /// case everywhere; no tracked capitals.
        enum TypeScale {
            /// The song's title at rest. Default tracking (no tightening).
            static let title = Font.system(size: 24, weight: .semibold)
            static let artist = Font.system(size: 15, weight: .regular)
            /// The album, the player's name, labels over a pane.
            static let meta = Font.system(size: 12, weight: .regular)
            /// The player's name over the song, a pane's heading.
            static let label = Font.system(size: 12, weight: .medium)
            /// A row's title in search and the queue; the song under the cover.
            static let rowTitle = Font.system(size: 13, weight: .semibold)
            static let rowSubtitle = Font.system(size: 11, weight: .regular)
            /// "Library", "Album", section names, counts.
            static let tag = Font.system(size: 11, weight: .medium)
            /// Times under the bar and in capsules.
            static let time = Font.system(size: 11, weight: .regular).monospacedDigit()
            static let timeStrong = Font.system(size: 11, weight: .semibold).monospacedDigit()
            /// The lyric shown over the bar while scrubbing.
            static let capsuleLyric = Font.system(size: 15, weight: .semibold)
            static let field = Font.system(size: 15, weight: .regular)
            /// A note over the card ("plays alone").
            static let note = Font.system(size: 12, weight: .medium)
        }

        // MARK: Shapes

        enum Radius {
            /// The card. Concentric with the cover: cover radius + padding.
            static let card: CGFloat = 28
            static let cover: CGFloat = 8
            /// Search field, capsules, the note.
            static let control: CGFloat = 10
            /// A lit row in search or the queue.
            static let row: CGFloat = 8
            /// Small covers in rows.
            static let thumb: CGFloat = 5
        }

        // MARK: Glyphs

        enum Glyph {
            /// Icon buttons (search, queue, film) and the × that closes a pane.
            static let icon = Font.system(size: 13, weight: .medium)
            /// The halo behind an icon button, and its hit target.
            static let button: CGFloat = 30
        }

        // MARK: Depth

        enum Shadow {
            /// The card resting on the wallpaper: the one soft shadow.
            static let cardColor = Color.black.opacity(0.32)
            static let cardRadius: CGFloat = 28
            static let cardY: CGFloat = 12
            /// The cover resting on the card.
            static let coverColor = Color.black.opacity(0.35)
            static let coverRadius: CGFloat = 12
            static let coverY: CGFloat = 6
        }

        // MARK: Motion

        /// The player's motion, all from `LunalyrDesign.Motion`: bounce-free
        /// springs, exits shorter than entrances, a fade under Reduce Motion.
        enum Motion {
            /// The card appearing (with an 8 pt rise) and leaving (a plain fade).
            static func reveal(shown: Bool, reduceMotion: Bool) -> Animation {
                if reduceMotion { return LunalyrDesign.Motion.reduced }
                return shown ? LunalyrDesign.Motion.standard : LunalyrDesign.Motion.exit
            }
            /// Song ↔ search ↔ queue.
            static func pane(reduceMotion: Bool) -> Animation {
                LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion)
            }
            /// Hover feedback, selection, a capsule or note arriving.
            static let feedback = LunalyrDesign.Motion.quick
            /// A capsule or highlight leaving.
            static let feedbackOut = LunalyrDesign.Motion.quickExit
            /// A button pressed in, and let go (no overshoot).
            static let press = LunalyrDesign.Motion.press
            static let release = LunalyrDesign.Motion.release
            /// The record sliding in and out of the sleeve.
            static let record = LunalyrDesign.Motion.emphasised
            /// The rise, in points, as the card appears.
            static let rise: CGFloat = 8
            /// How far a button sinks when pressed.
            static let pressScale: CGFloat = 0.94
        }
    }
}
