#ifndef VERSO_CUSTOM_GEOMETRY_H
#define VERSO_CUSTOM_GEOMETRY_H
#ifdef __cplusplus
extern "C" {
#endif
typedef struct { double x, y, width, height; } VCBox;
typedef struct { double ax, ay, bx, by; int from, to; } VCSegment;
typedef struct { double start, end; } VCVocalWindow;
typedef struct { double start, end; int nextVocal; } VCVocalGap;
/* One bounded memo per canvas, initialized to {0}. No heap or global state. */
typedef struct {
    VCBox boxes[64], viewport;
    VCSegment segments[63];
    double clearance;
    int count, written, valid;
    unsigned long builds;
} VCConnectionCache;
int vc_cached_connections(VCConnectionCache *cache, const VCBox *boxes, int count,
                         VCBox viewport, double clearance, VCSegment *output, int capacity);
int vc_vocal_gap(const VCVocalWindow *vocals, int count, double time,
                 double track_duration, double minimum_gap, VCVocalGap *output);
/* Fixed ink envelope: independent of the current word's animation phase. */
VCBox vc_word_envelope(double width, double height, double peak_scale, double degrees);
/* Seek-safe word arrival, 0 before its timestamp and 1 after its entrance. */
double vc_word_reveal(double time, double start, double end);
double vc_word_visibility(double time, double start, double end, int preview_enabled, int next_word);
double vc_word_settle(double progress);
/* Conservative fallback for line-only lyrics spanning a long instrumental. */
double vc_estimated_line_end(double start, double next_start, int word_count, double seconds_per_word);
/* Text-to-text links, clipped outside padded glyph bounds and inside the canvas.
   Invalid, overlapping and off-screen nodes never produce a link. */
int vc_build_connections(const VCBox *boxes, int count, VCBox viewport,
                         double clearance, VCSegment *output, int capacity);
/* Deterministic metadata scorer used by the native lyrics resolver. */
double vc_match_score(double title_match, double artist_match, double duration_delta,
                      int duration_known, int word_timed, int line_timed, int album_match);
#ifdef __cplusplus
}
#endif
#endif
