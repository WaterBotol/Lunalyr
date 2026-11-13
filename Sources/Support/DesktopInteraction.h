#ifndef VERSO_DESKTOP_INTERACTION_H
#define VERSO_DESKTOP_INTERACTION_H
#include <stdint.h>
#include "ConstellationGeometry.h"
typedef struct { double x, y; } VCPoint;
typedef struct { uint32_t displayID; VCBox frame; } VCDisplayFrame;
typedef struct { uint32_t activeDisplayID; double deadline; } VCHoverSession;
/* AppKit global coordinates, including negative/stacked screen origins. */
VCBox vc_hover_hot_zone(VCBox screen);
/* The player card's size, centred on each screen. Once set (> 0), the reveal
   zone is the card grown by VC_HOVER_REVEAL_MARGIN and the zone that keeps a
   revealed player up is the card grown by VC_HOVER_KEEP_MARGIN, instead of
   the middle 30 % of the screen and the whole control panel. */
#define VC_HOVER_REVEAL_MARGIN 24.0
#define VC_HOVER_KEEP_MARGIN 56.0
void vc_hover_set_card(double width, double height);
VCBox vc_hover_keep_zone(VCBox screen);
VCBox vc_control_panel_frame(VCBox screen);
int vc_display_at_point(const VCDisplayFrame *screens, int count, VCPoint point);
/* Classify the actual mouse-down target supplied by AppKit, not every window
   whose bounding rectangle covers the point. Unknown positive IDs stay blocked. */
int vc_hit_exposes_desktop(uint32_t window_id, int layer_known, int32_t layer,
                            int32_t normal_layer, int own_desktop_window);
/* An occluding app or a change of display cancels the previous hover at once. */
uint32_t vc_hover_update(VCHoverSession *session, const VCDisplayFrame *screens,
                         int count, VCPoint mouse, int blocked, double now, double linger);
/* As vc_hover_update, but a held mouse button can never start a reveal or move
   it to another display: rubber-band selections and file drags pass through.
   A reveal that is already active (scrubbing) keeps its display and linger. */
uint32_t vc_hover_step(VCHoverSession *session, const VCDisplayFrame *screens,
                       int count, VCPoint mouse, int blocked, int buttons_held,
                       double now, double linger);

/* Desktop coverage, in any one consistent coordinate space. At most
   VC_MAX_COVERS rectangles are considered; callers pass the largest first. */
#define VC_MAX_COVERS 64
/* Uncovered share of `region` (0...1). Invalid regions count as fully exposed. */
double vc_exposed_fraction(VCBox region, const VCBox *covers, int count);
/* 1 when the covers leave no axis-aligned square of side `min_gap` exposed
   inside `region` and expose at most `max_exposed` of its area. Thin slivers
   (window margins, tiling gaps) are ignored; invalid input is never covered. */
int vc_region_covered(VCBox region, const VCBox *covers, int count,
                      double min_gap, double max_exposed);
#endif
