#ifndef VERSO_LYRIC_MOTION_H
#define VERSO_LYRIC_MOTION_H
#include <stdint.h>
/* Coordinates are in lyric point-size units, independent of screen pixels. */
typedef struct { double x, y, scale, angle, opacity, blur; } VCWordPose;
double vc_motion_ease(double progress);
double vc_motion_handoff(double age, double cadence);
double vc_rolling_rank_opacity(double rank);
VCWordPose vc_rolling_pose(int style, double rank, double age, double duration,
                          uint64_t seed, double energy, int reduce_motion);
/* The vertical place vc_rolling_pose gives a rank before breathing: the
   pose's y is this plus the breath. Stages that space words by their
   measured size use the difference as a small per-word lift. */
double vc_rolling_baseline(int style, double rank);
VCWordPose vc_vocal_gesture(double age, double duration, uint64_t seed,
                           double energy, int reduce_motion);

/* ---- Organic motion ------------------------------------------------------
   Every function below is either a pure function of time (seek-safe and
   identical at any frame rate) or an exactly integrated filter whose result
   does not depend on how often it is sampled. */

/* A damped spring: natural angular frequency (rad/s) and damping ratio. */
typedef struct { double omega, zeta; } VCSpring;

/* Step response of a spring released from rest at age 0 towards 1. Closed
   form, so a target that changes again before the first move settles is the
   sum of two step responses: velocity carries across the interruption.
   Lands on exactly 1 once the residual is below 1e-4. */
double vc_spring_step(double age, VCSpring spring);
/* Derivative of vc_spring_step with respect to age. */
double vc_spring_velocity(double age, VCSpring spring);
/* Monotone critically damped arrival that lands on exactly 1 at duration. */
double vc_settle(double age, double duration);
/* Smooth seeded gradient noise in [-1, 1], continuous in value and slope. */
double vc_noise(uint64_t seed, double t);
/* Two octaves of vc_noise: less regular, still smooth. */
double vc_noise2(uint64_t seed, double t);
/* Seeded value in [-1, 1) for per-word variation. */
double vc_jitter(uint64_t seed, int channel);

/* A spring tuned to one sung word: short syllables snap, held notes bloom,
   energy loosens the damping, and the seed varies every word a little. */
VCSpring vc_word_spring(uint64_t seed, double duration, double cadence, double energy);

typedef struct {
    double reveal;  /* 0...1, monotone: opacity, blur and masks */
    double enter;   /* spring arrival for travel, scale and turn; may overshoot */
    double heat;    /* vocal emphasis: rises into the word, releases after it */
    double leave;   /* 0...1, this word's share of the line exit */
    double x, y, scale, angle; /* vocal gesture while the word is sung */
} VCWordMotion;

/* Everything one timed word does, as a pure function of playback time.
   exit_start is the line's exit onset (INFINITY when it has none) and order
   the word's reading position in its line, 0...1, which staggers the exit. */
VCWordMotion vc_word_motion(double time, double start, double end, double cadence,
                            double exit_start, double order, uint64_t seed,
                            double energy, int reduce_motion);
/* Rolling layouts: one onset's share of the cursor, as a seeded spring. */
double vc_rolling_handoff(double age, double cadence, uint64_t seed, double energy);
/* Slow seeded float for settled words, in point-size units and degrees.
   Amplitude 0 returns exact rest. */
VCWordPose vc_idle_drift(uint64_t seed, double time, double amplitude);

/* Playback position smoothed for display. Small re-anchoring errors are
   absorbed by briefly bending the rate (never more than a few per cent);
   real jumps are adopted at once and reported so the scene can cross-fade. */
typedef struct { double time, wall; int valid; } VCLyricClock;
int vc_lyric_clock_advance(VCLyricClock *clock, double wall, double raw, int playing);

/* Critically damped follower, exactly integrated over dt, then limited to
   max_rate units per second. Used for colour and audio so neither can flash. */
typedef struct { double value, velocity; int valid; } VCFollower;
double vc_follow(VCFollower *follower, double target, double dt, double tau, double max_rate);
#endif
