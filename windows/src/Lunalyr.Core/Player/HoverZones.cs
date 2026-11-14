// Ported from the Mac's card zones in Sources/Support/DesktopInteraction.c (vc_hover_set_card, card_zone and the card
// branches of vc_hover_hot_zone and vc_hover_keep_zone) and their use in Sources/Desktop/HoverState.swift. Those lines
// are Lunalyr's own (commit d5f73c92, Test Build 88), not the Verso import, so they are translated; the hover step
// around them stays this port's own (DesktopHoverSession.cs).

namespace Lunalyr.Core.Player;

/// <summary>
/// Where the pointer reveals the player and where it keeps a shown player up: the card as it is now (at its current
/// height), centred where the card is drawn, grown by a margin and never past that area.
/// </summary>
/// <remarks>
/// The Mac's card is centred on its screen; the Windows card is centred in the display's work area, so its zones are
/// centred there too. Before Test Build 88 both apps used larger zones (the Mac: the middle 30 % of the screen and a
/// 780-pt square).
/// </remarks>
public static class HoverZones
{
    /// <summary>The card grown by this much on every side starts a reveal (DIPs).</summary>
    public const double RevealMargin = 24;

    /// <summary>The card grown by this much on every side keeps a shown player up without lingering (DIPs).</summary>
    public const double KeepMargin = 56;

    /// <summary>
    /// A <paramref name="cardWidth"/> × <paramref name="cardHeight"/> card centred on <paramref name="area"/>, grown by
    /// <paramref name="margin"/> on every side, kept within <paramref name="area"/>. All in the same units.
    /// </summary>
    public static HoverRect CardZone(HoverRect area, double cardWidth, double cardHeight, double margin)
    {
        var width = Math.Min(area.Width, cardWidth + 2 * margin);
        var height = Math.Min(area.Height, cardHeight + 2 * margin);
        return new HoverRect(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2,
            area.Left + (area.Width + width) / 2, area.Top + (area.Height + height) / 2);
    }

    /// <summary>The reveal zone for a card in DIPs on an area in pixels at <paramref name="scale"/> pixels a DIP.</summary>
    public static HoverRect Reveal(HoverRect area, double cardWidth, double cardHeight, double scale = 1) =>
        CardZone(area, cardWidth * scale, cardHeight * scale, RevealMargin * scale);

    /// <summary>The keep zone for a card in DIPs on an area in pixels at <paramref name="scale"/> pixels a DIP.</summary>
    public static HoverRect Keep(HoverRect area, double cardWidth, double cardHeight, double scale = 1) =>
        CardZone(area, cardWidth * scale, cardHeight * scale, KeepMargin * scale);
}
