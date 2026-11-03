#ifndef VERSO_LYRIC_MOTION_H
#define VERSO_LYRIC_MOTION_H
#include <stdint.h>
/* Coordinates are in lyric point-size units, independent of screen pixels. */
typedef struct { double x, y, scale, angle, opacity, blur; } VCWordPose;
double vc_motion_ease(double progress);
double vc_motion_handoff(double age, double cadence);
VCWordPose vc_rolling_pose(int style, double rank, double age, double duration,
                          uint64_t seed, double energy, int reduce_motion);
VCWordPose vc_vocal_gesture(double age, double duration, uint64_t seed,
                           double energy, int reduce_motion);
#endif
