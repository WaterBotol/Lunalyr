import AppKit
import SwiftUI

// MARK: - Redesign tokens ("On the picture")
//
// The window language of the redesign: work/ui-redesign-mockups/final/SPEC.md
// (sections 1 and 2) is the source; every value here is from it. Light and
// dark are resolved per appearance; Increase Contrast darkens secondary text
// and adds edges where the spec's fills alone separate regions.
//
//     Text(caption).font(LunalyrDesign.Studio.TypeScale.caption)
//         .foregroundStyle(Color.studio(.ink2))

extension LunalyrDesign {
    enum Studio {
        enum Role: CaseIterable {
            case ink, ink2, accent, accentText
            case sidebar, canvas, window, control, controlOnCanvas
            case sidebarSelected, segmentSelected, popover
            case checkboxOff, checkboxEdge, track, separator, searchFill
            case success, warning
        }

        /// The colour for `role` in one appearance.
        static func color(_ role: Role, dark: Bool, increaseContrast: Bool = false) -> NSColor {
            func hex(_ value: UInt32, _ alpha: CGFloat = 1) -> NSColor {
                NSColor(srgbRed: CGFloat((value >> 16) & 0xFF) / 255, green: CGFloat((value >> 8) & 0xFF) / 255,
                        blue: CGFloat(value & 0xFF) / 255, alpha: alpha)
            }
            let base: NSColor = dark ? .white : .black
            switch role {
            case .ink: return dark ? hex(0xF2F2F5) : hex(0x1C1C1F)
            case .ink2:
                if increaseContrast { return dark ? hex(0xCDCDD3) : hex(0x3F3F44) }
                return dark ? hex(0xA3A3AB) : hex(0x5E5E63)
            case .accent: return dark ? hex(0x0A85FF) : hex(0x0075F5)
            case .accentText: return dark ? hex(0x5CABFF) : hex(0x0066D6)
            case .sidebar: return dark ? hex(0x2A2A2D) : hex(0xF4F4F6)
            case .canvas: return dark ? hex(0x131315) : hex(0xE8E8EB)
            case .window: return dark ? hex(0x1E1E20) : hex(0xFFFFFF)
            case .control: return dark ? hex(0x343438) : hex(0xECECEF)
            case .controlOnCanvas: return dark ? hex(0x2B2B2F) : hex(0xDADADE)
            case .sidebarSelected: return dark ? hex(0x404044) : hex(0xE2E2E5)
            case .segmentSelected: return dark ? hex(0x5C5C61) : hex(0xFFFFFF)
            case .popover: return dark ? hex(0x2B2B2E) : hex(0xFFFFFF)
            case .checkboxOff: return dark ? hex(0x38383C) : hex(0xFFFFFF)
            case .checkboxEdge: return base.withAlphaComponent(dark ? 0.32 : 0.28)
            case .track: return base.withAlphaComponent(dark ? 0.16 : 0.10)
            case .separator: return base.withAlphaComponent(dark ? 0.12 : 0.10)
            case .searchFill: return base.withAlphaComponent(dark ? 0.08 : 0.055)
            case .success: return dark ? hex(0x30D158) : hex(0x248A3D)
            case .warning: return dark ? hex(0xFF9E0A) : hex(0xB26100)
            }
        }

        /// Edge drawn between regions under Increase Contrast only (fills separate them otherwise).
        static func contrastEdge(dark: Bool) -> NSColor {
            (dark ? NSColor.white : NSColor.black).withAlphaComponent(0.5)
        }

        // MARK: Layout (points)
        enum Layout {
            static let sidebarWidth: CGFloat = 186
            static let inspectorWidth: CGFloat = 286
            static let inspectorPadding = EdgeInsets(top: 16, leading: 20, bottom: 16, trailing: 20)
            static let defaultWindow = CGSize(width: 1100, height: 720)
            static let searchHeight: CGFloat = 28
            static let searchRadius: CGFloat = 7
            static let sidebarRowHeight: CGFloat = 28
            static let sidebarRowRadius: CGFloat = 6
            static let sidebarInset: CGFloat = 10
            /// Inspector: between sections, and between rows inside one.
            static let inspectorSectionSpacing: CGFloat = 22
            static let inspectorRowSpacing: CGFloat = 9
            /// Pages without a picture: between sections, and between rows.
            static let pageSectionSpacing: CGFloat = 28
            static let pageRowSpacing: CGFloat = 8
            /// A caption sits this close under its control.
            static let captionGap: CGFloat = 3
        }

        // MARK: Type (SF Pro, regular and semibold only)
        enum TypeScale {
            static let title = Font.system(size: 17, weight: .semibold)
            static let section = Font.system(size: 13, weight: .semibold)
            static let row = Font.system(size: 13, weight: .regular)
            static let caption = Font.system(size: 11.5, weight: .regular)
            static let value = Font.system(size: 12, weight: .regular).monospacedDigit()
            static let tile = Font.system(size: 11, weight: .regular)
            static let typefaceTile = Font.system(size: 10.5, weight: .regular)
            static let sidebarGroup = Font.system(size: 11.5, weight: .semibold)
        }
    }
}

extension Color {
    /// A redesign colour role, resolved for the current appearance and contrast.
    static func studio(_ role: LunalyrDesign.Studio.Role) -> Color {
        Color(nsColor: NSColor(name: "LunalyrStudio.\(role)") { appearance in
            let look = LunalyrAppearance(appearance)
            return LunalyrDesign.Studio.color(role, dark: look.dark, increaseContrast: look.increaseContrast)
        })
    }
}
