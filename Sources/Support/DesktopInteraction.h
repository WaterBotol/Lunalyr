#ifndef VERSO_DESKTOP_INTERACTION_H
#define VERSO_DESKTOP_INTERACTION_H
#include <stdint.h>
#include "ConstellationGeometry.h"
typedef struct { double x, y; } VCPoint;
typedef struct { uint32_t displayID; VCBox frame; } VCDisplayFrame;
typedef struct { uint32_t activeDisplayID; double deadline; } VCHoverSession;
/* AppKit global coordinates, including negative/stacked screen origins. */
VCBox vc_hover_hot_zone(VCBox screen);
VCBox vc_control_panel_frame(VCBox screen);
int vc_display_at_point(const VCDisplayFrame *screens, int count, VCPoint point);
/* Classify the actual mouse-down target supplied by AppKit, not every window
   whose bounding rectangle covers the point. Unknown positive IDs stay blocked. */
int vc_hit_exposes_desktop(uint32_t window_id, int layer_known, int32_t layer,
                            int32_t normal_layer, int own_desktop_window);
/* An occluding app or a change of display cancels the previous hover at once. */
uint32_t vc_hover_update(VCHoverSession *session, const VCDisplayFrame *screens,
                         int count, VCPoint mouse, int blocked, double now, double linger);
#endif
