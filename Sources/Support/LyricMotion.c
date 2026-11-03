#include "LyricMotion.h"
#include <math.h>

static double clamp(double x, double low, double high) {
    return isfinite(x) ? fmin(high, fmax(low, x)) : low;
}
static double smooth(double x) { x = clamp(x, 0, 1); return x*x*(3-2*x); }
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
VCWordPose vc_rolling_pose(int style, double rank, double age, double duration,
                          uint64_t seed, double energy, int reduced) {
    rank = clamp(rank, -2, 9); age = clamp(age, -60, 3600);
    duration = clamp(duration, .06, 30); energy = clamp(energy, 0, 1);
    double h = fmax(0, rank), phase = (double)(seed % 1021) / 1021 * 6.2831853;
    double focus = exp(-rank*rank*2.4);
    double release = 1-smooth((age-duration-.45)/1.0);
    double fade = (1-smooth((h-2.4)/1.7)) * release;
    /* Extra clearance around the hero, then a tighter historical tail.
       Unlike a fixed row height, this leaves room for large adjacent words. */
    double separation = .66*rank+(style == 2 ? .86 : .70)*tanh(rank*1.2);
    VCWordPose p = {0, 0, 1, 0, fade, 0};
    if (style == 0) {
        /* Hero word, then a loose fan of receding fragments. */
        p.x = sin(h*.92+phase*.22) * fmin(h, 1) * .70;
        p.y = -separation;
        p.scale = .55+.49*focus;
        p.angle = sin(phase+h*.6)*fmin(h, 1)*2.4;
    } else if (style == 1) {
        /* Uneven, flowing column: differences in type do the emphasis. */
        p.x = sin(rank*.72+phase*.12)*.28;
        p.y = .35-separation;
        p.scale = .86+.18*focus;
        p.angle = sin(phase)*1.3*(1-focus);
    } else {
        /* Labels travel into a loose collage rather than a masked ticker. */
        p.x = sin(rank*.83+phase*.28)*.66;
        p.y = .30-separation;
        p.scale = .82+.22*focus;
        p.angle = sin(phase+rank*.35)*(2.0+3.3*(1-focus));
    }
    if (!reduced && age >= 0) {
        double sustain = smooth((duration-.35)/1.4);
        double vocalArc = sin(clamp(age/duration, 0, 1)*3.141592653589793);
        double breath = sin(age*(2.0+energy*1.8)+phase)*sustain;
        p.y += breath*.055*focus;
        p.scale += focus*(sustain*vocalArc*.12+energy*.07);
        p.angle += sin(age*2.6+phase)*energy*.75*focus;
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
    double vocalArc = sin(clamp(age/duration, 0, 1)*3.141592653589793);
    double wind = sin(age*(2.2+energy)+phase)*held;
    p.x = sin(age*1.65+phase)*.025*held*heat*arrive;
    p.y = (-.13*held*vocalArc+wind*.055-energy*.035)*heat*arrive;
    p.scale = 1+(held*vocalArc*.12+energy*.045)*heat*arrive;
    p.angle = wind*1.2*heat*arrive;
    return p;
}
