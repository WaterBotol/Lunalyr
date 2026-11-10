#include "LyricMotion.h"
#include <math.h>

static const double PI = 3.141592653589793;

static double clamp(double x, double low, double high) {
    return isfinite(x) ? fmin(high, fmax(low, x)) : low;
}
static double smooth(double x) { x = clamp(x, 0, 1); return x*x*(3-2*x); }

/* ---- Seeded variation ---------------------------------------------------- */

static uint64_t mix64(uint64_t x) {
    x += 0x9E3779B97F4A7C15ULL;
    x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9ULL;
    x = (x ^ (x >> 27)) * 0x94D049BB133111EBULL;
    return x ^ (x >> 31);
}
static double lattice(uint64_t seed, int64_t index) {
    uint64_t h = mix64(seed ^ ((uint64_t)index * 0xD1B54A32D192ED03ULL));
    return (double)(h >> 11) * (2.0 / 9007199254740992.0) - 1.0;
}
double vc_jitter(uint64_t seed, int channel) {
    return lattice(mix64(seed + 0x632BE59BD9B4E019ULL), channel);
}
double vc_noise(uint64_t seed, double t) {
    if (!isfinite(t) || fabs(t) > 4.0e15) return 0;
    double whole = floor(t), f = t - whole;
    int64_t i = (int64_t)whole;
    double g0 = lattice(seed, i), g1 = lattice(seed, i + 1);
    double u = f*f*f*(f*(f*6-15)+10);
    /* Gradient noise peaks at 0.5 for unit gradients. */
    return clamp(2*((1-u)*g0*f + u*g1*(f-1)), -1, 1);
}
double vc_noise2(uint64_t seed, double t) {
    return clamp((vc_noise(seed, t) + .5*vc_noise(mix64(seed), t*2.07 + 19.31)) / 1.5, -1, 1);
}

/* ---- Springs ---------------------------------------------------------- */

static VCSpring sane(VCSpring s) {
    if (!isfinite(s.omega) || s.omega <= 0) s.omega = 12;
    if (!isfinite(s.zeta) || s.zeta <= 0) s.zeta = 1;
    s.omega = clamp(s.omega, .5, 200);
    s.zeta = clamp(s.zeta, .05, 4);
    return s;
}
/* After this age the residual is provably below 1e-4. */
static double spring_cutoff(VCSpring s) {
    double z = s.zeta, w = s.omega;
    if (z < .999) {
        double root = sqrt(1 - z*z);
        return log(1e4 / root) / (z*w);
    }
    if (z <= 1.001) return 12.5 / w;
    double q = sqrt(z*z - 1), r1 = -w*(z - q), r2 = -w*(z + q);
    double bound = (fabs(r1) + fabs(r2)) / fabs(r1 - r2);
    return log(bound * 1e4) / fabs(r1);
}
double vc_spring_step(double age, VCSpring spring) {
    if (!isfinite(age) || age <= 0) return 0;
    VCSpring s = sane(spring);
    if (age >= spring_cutoff(s)) return 1;
    double z = s.zeta, w = s.omega;
    if (z < .999) {
        double root = sqrt(1 - z*z), wd = w*root;
        return 1 - exp(-z*w*age)*(cos(wd*age) + z/root*sin(wd*age));
    }
    if (z <= 1.001) return 1 - exp(-w*age)*(1 + w*age);
    double q = sqrt(z*z - 1), r1 = -w*(z - q), r2 = -w*(z + q);
    return 1 + (r2*exp(r1*age) - r1*exp(r2*age)) / (r1 - r2);
}
double vc_spring_velocity(double age, VCSpring spring) {
    if (!isfinite(age) || age <= 0) return 0;
    VCSpring s = sane(spring);
    if (age >= spring_cutoff(s)) return 0;
    double z = s.zeta, w = s.omega;
    if (z < .999) {
        double root = sqrt(1 - z*z);
        return w/root*exp(-z*w*age)*sin(w*root*age);
    }
    if (z <= 1.001) return w*w*age*exp(-w*age);
    double q = sqrt(z*z - 1), r1 = -w*(z - q), r2 = -w*(z + q);
    return r1*r2*(exp(r1*age) - exp(r2*age)) / (r1 - r2);
}
double vc_settle(double age, double duration) {
    if (!isfinite(age) || age <= 0) return 0;
    if (!isfinite(duration) || duration <= 0 || age >= duration) return 1;
    /* Critically damped from rest; the small remaining tail is folded away
       over the last 40%, so the curve stays monotone and finishes exactly. */
    double w = 9 / duration, residual = exp(-w*age)*(1 + w*age);
    double window = 1 - smooth((age - .6*duration) / (.4*duration));
    return clamp(1 - residual*window, 0, 1);
}

VCSpring vc_word_spring(uint64_t seed, double duration, double cadence, double energy) {
    duration = clamp(duration, .04, 30);
    cadence = isfinite(cadence) && cadence > 0 ? clamp(cadence, .04, 30) : duration;
    energy = clamp(energy, 0, 1);
    double held = smooth((duration - .35) / 1.1);
    /* Rise time follows the syllable. Critically damped, 10-90% rise is
       about 3.36 / omega, so omega is derived from the rise directly. */
    double rise = clamp(.42*fmin(duration, cadence) + .07, .10, .34)
        * (1 + .14*vc_jitter(seed, 7)) * (1 + .35*held);
    VCSpring s = {3.36 / rise, clamp(.78 - .12*energy + .14*held + .06*vc_jitter(seed, 8), .58, 1)};
    return s;
}

VCWordMotion vc_word_motion(double time, double start, double end, double cadence,
                            double exit_start, double order, uint64_t seed,
                            double energy, int reduced) {
    VCWordMotion m = {0, 0, 0, 0, 0, 0, 1, 0};
    if (!isfinite(time) || !isfinite(start)) return m;
    if (!isfinite(end) || end < start) end = start;
    double duration = clamp(end - start, .04, 30), age = time - start;
    energy = clamp(energy, 0, 1);
    double reveal = clamp(.65*duration, .16, .46) * (1 + .08*vc_jitter(seed, 1));
    m.reveal = vc_settle(age, reveal);
    m.enter = reduced ? m.reveal : vc_spring_step(age, vc_word_spring(seed, duration, cadence, energy));
    /* Emphasis gathers with the onset (a breath ahead of it) and releases
       when the voice leaves the word. The release is a second step on the same spring, so
       velocity carries through it, and a clipped syllable carries less
       weight than a held one. */
    double rise = clamp(.32*duration + .05, .07, .22) * (1 + .1*vc_jitter(seed, 2));
    VCSpring swell = {3.36 / rise, clamp(.72 + .08*vc_jitter(seed, 3) - .08*energy, .55, .95)};
    /* The voice lets go more slowly than it arrives: a longer, better
       damped release leaves a short afterglow and never rings below rest. */
    VCSpring fall = {3.36 / (rise*1.35), clamp(.86 + .05*vc_jitter(seed, 11), .8, .95)};
    double release = fmax(end, start + .08);
    double weight = .72 + .28*smooth((duration - .08) / .5);
    m.heat = clamp(weight*(vc_spring_step(time - (start - .02), swell) - vc_spring_step(time - release, fall)), 0, 1.15);
    if (isfinite(exit_start)) {
        double stagger = clamp(order, 0, 1)*.14 + (.5 + .5*vc_jitter(seed, 4))*.05;
        /* A word stays a moment once it has fully arrived: in a very fast
           line it otherwise left about a tenth of a second after its onset,
           a flash rather than a word. */
        double leaves = fmax(exit_start + stagger, start + reveal + .20);
        m.leave = vc_settle(time - leaves, .40*(1 + .1*vc_jitter(seed, 5)));
    }
    if (reduced || m.heat <= 0) return m;
    double hot = fmin(m.heat, 1), held = smooth((duration - .25) / 1.1);
    double arc = sin(clamp(age / duration, 0, 1)*PI);
    double wind = vc_noise2(seed, age*(.75 + .4*energy))*held;
    m.x = vc_noise(seed ^ 0x9E37ULL, age*.6)*.025*held*hot;
    m.y = (-.13*held*arc + wind*.055 - energy*.035)*hot;
    m.scale = 1 + (held*arc*.12 + energy*.045)*hot;
    m.angle = wind*1.2*hot;
    return m;
}

double vc_rolling_handoff(double age, double cadence, uint64_t seed, double energy) {
    if (!isfinite(age) || age <= 0) return 0;
    cadence = isfinite(cadence) ? clamp(cadence, .06, 30) : .4;
    double rise = clamp(cadence*.5, .12, .36) * (1 + .1*vc_jitter(seed, 9));
    VCSpring s = {3.36 / rise, clamp(.86 - .08*clamp(energy, 0, 1) + .05*vc_jitter(seed, 10), .74, 1)};
    return vc_spring_step(age, s);
}

VCWordPose vc_idle_drift(uint64_t seed, double time, double amplitude) {
    VCWordPose p = {0, 0, 1, 0, 1, 0};
    if (!isfinite(time) || !isfinite(amplitude) || amplitude <= 0) return p;
    amplitude = fmin(amplitude, 1.5);
    double rate = .13 + .06*(.5 + .5*vc_jitter(seed, 6));
    p.x = vc_noise2(seed ^ 0xA1ULL, time*rate)*.018*amplitude;
    p.y = vc_noise2(seed ^ 0xB2ULL, time*rate*1.17)*.024*amplitude;
    p.angle = vc_noise(seed ^ 0xC3ULL, time*rate*.8)*.45*amplitude;
    p.scale = 1 + vc_noise(seed ^ 0xD4ULL, time*rate*.9)*.006*amplitude;
    return p;
}

/* ---- Clock and follower ------------------------------------------------ */

int vc_lyric_clock_advance(VCLyricClock *c, double wall, double raw, int playing) {
    if (!c || !isfinite(wall) || !isfinite(raw)) return 0;
    if (!c->valid || !isfinite(c->time) || !isfinite(c->wall)) {
        c->time = raw; c->wall = wall; c->valid = 1;
        return 0;
    }
    double dt = wall - c->wall;
    if (dt < -1) { c->time = raw; c->wall = wall; return 1; }
    /* Repeated evaluation of one frame, or a slightly older display. */
    if (dt <= 0) return 0;
    c->wall = wall;
    dt = fmin(dt, .25);
    double predicted = c->time + (playing ? dt : 0);
    double error = raw - predicted;
    if (fabs(error) > .30) { c->time = raw; return 1; }
    double pull = error*(1 - exp(-dt / .45));
    if (playing) {
        /* Bend the rate, never reverse it: at most a few per cent for the
           usual re-anchoring jitter, more only for a larger correction. */
        double cap = (.06 + .5*fabs(error))*dt;
        pull = clamp(pull, -cap, cap);
    } else {
        pull = clamp(pull, 0, .5*dt);
    }
    c->time = predicted + pull;
    return 0;
}

double vc_follow(VCFollower *f, double target, double dt, double tau, double max_rate) {
    if (!f) return 0;
    if (!isfinite(target)) target = f->valid && isfinite(f->value) ? f->value : 0;
    if (!f->valid || !isfinite(f->value) || !isfinite(f->velocity)) {
        f->value = target; f->velocity = 0; f->valid = 1;
        return target;
    }
    if (!isfinite(dt) || dt <= 0) return f->value;
    dt = fmin(dt, 1);
    double w = 1 / (isfinite(tau) && tau > 1e-4 ? tau : 1e-4);
    double y0 = f->value - target, v0 = f->velocity, e = exp(-w*dt);
    double next = target + (y0 + (v0 + w*y0)*dt)*e;
    double velocity = (v0 - w*(v0 + w*y0)*dt)*e;
    if (isfinite(max_rate) && max_rate > 0) {
        double step = max_rate*dt;
        if (next - f->value > step) { next = f->value + step; velocity = fmin(velocity, max_rate); }
        else if (f->value - next > step) { next = f->value - step; velocity = fmax(velocity, -max_rate); }
    }
    f->value = next; f->velocity = velocity;
    return next;
}

/* ---- Original curves, kept for compatibility -------------------------- */

double vc_motion_ease(double t) {
    t = clamp(t, 0, 1);
    if (t == 0 || t == 1) return t;
    /* Damped arrival, with a small overshoot and no accumulated frame state. */
    double value = 1-exp(-7*t)*(cos(10*t)+.7*sin(10*t));
    double endpoint = 1-exp(-7)*(cos(10)+.7*sin(10));
    return clamp(value / endpoint, 0, 1.12);
}
double vc_motion_handoff(double age, double cadence) {
    if (!isfinite(age) || age <= 0) return 0;
    double duration = clamp(cadence * .82, .20, .58);
    double t = clamp(age / duration, 0, 1);
    /* The whole cluster follows each onset; held words do not scroll away. */
    return t*t*t*(10+t*(-15+6*t));
}
double vc_rolling_baseline(int style, double rank) {
    rank = clamp(rank, -2, 9);
    double separation = .66*rank+(style == 2 ? .86 : .70)*tanh(rank*1.2);
    return (style == 1 ? .35 : style == 2 ? .30 : 0) - separation;
}
double vc_rolling_rank_opacity(double rank) {
    return 1-smooth((clamp(rank, 0, 9)-2.4)/1.7);
}

VCWordPose vc_rolling_pose(int style, double rank, double age, double duration,
                          uint64_t seed, double energy, int reduced) {
    rank = clamp(rank, -2, 9); age = clamp(age, -60, 3600);
    duration = clamp(duration, .06, 30); energy = clamp(energy, 0, 1);
    double h = fmax(0, rank), phase = (double)(seed % 1021) / 1021 * 6.2831853;
    double focus = exp(-rank*rank*2.4);
    double release = 1-smooth((age-duration-.45)/1.0);
    double fade = vc_rolling_rank_opacity(h) * release;
    /* Extra clearance around the hero, then a tighter historical tail.
       Unlike a fixed row height, this leaves room for large adjacent words. */
    double separation = .66*rank+(style == 2 ? .86 : .70)*tanh(rank*1.2);
    VCWordPose p = {0, 0, 1, 0, fade, 0};
    if (style == 0) {
        /* Hero word, then a loose fan of receding fragments. */
        p.x = sin(h*.92+phase*.22) * fmin(h, 1) * .70;
        p.y = -separation;
        p.scale = .70+.34*focus;
        p.angle = sin(phase+h*.6)*fmin(h, 1)*2.4;
    } else if (style == 1) {
        /* Uneven, flowing column: differences in type do the emphasis. */
        p.x = sin(rank*.72+phase*.12)*.28;
        p.y = .35-separation;
        p.scale = .94+.10*focus;
        p.angle = sin(phase)*1.3*(1-focus);
    } else {
        /* Labels travel into a loose collage rather than a masked ticker. */
        p.x = sin(rank*.72+phase*.25)*.53;
        p.y = .30-separation;
        p.scale = .94+.10*focus;
        p.angle = sin(phase+rank*.28)*(1.6+2.5*(1-focus));
    }
    if (!reduced && age >= 0) {
        /* Noise, not a sine: zero at the onset (no step), never periodic, and
           seeded per timed word so two lines never breathe in unison. */
        double sustain = smooth((duration-.35)/1.4);
        double vocalArc = sin(clamp(age/duration, 0, 1)*PI);
        double attack = smooth(age/.12);
        double breath = vc_noise2(seed, age*(.32+energy*.3))*sustain*attack;
        p.y += breath*.055*focus;
        p.scale += focus*(sustain*vocalArc*.12+energy*.07*attack);
        p.angle += vc_noise(seed ^ 0x5151ULL, age*.42)*energy*.75*focus;
        p.blur = smooth((h-2.4)/1.7)*1.6;
    }
    if (reduced) { p.angle = 0; p.blur = 0; }
    return p;
}
VCWordPose vc_vocal_gesture(double age, double duration, uint64_t seed,
                           double energy, int reduced) {
    VCWordPose p = {0, 0, 1, 0, 1, 0};
    if (reduced || !isfinite(age) || age < 0) return p;
    duration = clamp(duration, .06, 30); energy = clamp(energy, 0, 1);
    double heat = 1-smooth((age-duration)/.65);
    if (heat <= 0) return p;
    double phase = (double)(seed % 997)/997*6.2831853;
    double held = smooth((duration-.25)/1.1), arrive = smooth(age/.18);
    double vocalArc = sin(clamp(age/duration, 0, 1)*PI);
    double wind = sin(age*(2.2+energy)+phase)*held;
    p.x = sin(age*1.65+phase)*.025*held*heat*arrive;
    p.y = (-.13*held*vocalArc+wind*.055-energy*.035)*heat*arrive;
    p.scale = 1+(held*vocalArc*.12+energy*.045)*heat*arrive;
    p.angle = wind*1.2*heat*arrive;
    return p;
}
