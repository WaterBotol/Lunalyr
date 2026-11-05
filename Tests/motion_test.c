#include "../Sources/Support/LyricMotion.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

static void original_curves(void) {
    assert(vc_motion_handoff(-1,.4)==0);
    assert(vc_motion_handoff(2,.4)==1);
    double previous=0;
    for(int i=0;i<=1000;i++) {
        double value=vc_motion_handoff(i*.001,.4);
        assert(value>=previous&&value<=1); previous=value;
        assert(vc_motion_ease(i*.001)>=0&&vc_motion_ease(i*.001)<=1.12);
    }
    for(int style=0;style<3;style++) {
        VCWordPose hidden=vc_rolling_pose(style,5,1,10,197,.6,0);
        assert(hidden.opacity==0); /* retires before it reaches a stage edge */
        VCWordPose a=vc_rolling_pose(style,0,.5,3,197,0,0);
        VCWordPose b=vc_rolling_pose(style,0,.5,3,197,1,0);
        assert(b.scale>a.scale); /* actual energy affects the active word */
        VCWordPose seek=vc_rolling_pose(style,0,.5,3,197,1,0);
        assert(memcmp(&seek,&b,sizeof(b))==0); /* independent of frame history */
        VCWordPose old=vc_rolling_pose(style,1,5,.4,197,1,0);
        assert(old.opacity==0); /* no stale words during a long gap */
        VCWordPose reduced=vc_rolling_pose(style,0,.5,3,197,1,1);
        assert(reduced.angle==0&&reduced.blur==0);
        VCWordPose hero=vc_rolling_pose(style,0,.2,.4,197,0,1);
        VCWordPose neighbour=vc_rolling_pose(style,1,.2,.4,197,0,1);
        assert(hero.y-neighbour.y>1.2); /* two large words have clearance */
        for(int i=0;i<900;i++) {
            double r=-1+i*.01;
            VCWordPose p=vc_rolling_pose(style,r,.5,2,197,.8,0);
            VCWordPose q=vc_rolling_pose(style,r+.00001,.5,2,197,.8,0);
            assert(fabs(p.y-q.y)<.001&&fabs(p.opacity-q.opacity)<.001);
            assert(fabs(p.scale-q.scale)<.001&&fabs(p.angle-q.angle)<.001);
            assert(isfinite(p.scale)&&p.scale>.3&&p.scale<1.5);
        }
        /* Without breathing, the pose sits exactly on its baseline. */
        VCWordPose calm=vc_rolling_pose(style,1.3,.5,2,197,.8,1);
        assert(fabs(calm.y-vc_rolling_baseline(style,1.3))<1e-12);
        assert(vc_rolling_baseline(style,0)>vc_rolling_baseline(style,1));
        /* The onset itself is continuous: breathing and energy start at zero. */
        VCWordPose before=vc_rolling_pose(style,0,-.00001,3,197,1,0);
        VCWordPose after=vc_rolling_pose(style,0,.00001,3,197,1,0);
        assert(fabs(before.y-after.y)<1e-4&&fabs(before.scale-after.scale)<1e-4);
        assert(fabs(before.angle-after.angle)<1e-4);
    }
    VCWordPose settled=vc_vocal_gesture(30,.4,101,.7,0);
    assert(settled.x==0&&settled.y==0&&settled.scale==1);
    VCWordPose first=vc_vocal_gesture(.7,2,101,.3,0);
    VCWordPose held=vc_vocal_gesture(1.2,2,101,.3,0);
    assert(first.y!=held.y);
    VCWordPose invalid=vc_rolling_pose(0,NAN,INFINITY,NAN,0,NAN,0);
    assert(isfinite(invalid.x)&&isfinite(invalid.y)&&isfinite(invalid.opacity));
}

static void springs(void) {
    VCSpring kinds[]={{14,.6},{14,1},{9,1.6},{30,.85}};
    for(int k=0;k<4;k++) {
        VCSpring s=kinds[k];
        assert(vc_spring_step(-1,s)==0&&vc_spring_step(0,s)==0);
        assert(vc_spring_step(20,s)==1); /* lands exactly, so idle frames compare equal */
        double previous=0, peak=0;
        for(int i=1;i<=4000;i++) {
            double t=i*.0005, x=vc_spring_step(t,s);
            assert(isfinite(x)&&fabs(x-previous)<.02); /* no jumps at 2 kHz */
            double numeric=(vc_spring_step(t+1e-6,s)-vc_spring_step(t-1e-6,s))/2e-6;
            assert(fabs(numeric-vc_spring_velocity(t,s))<1e-3*fmax(1,fabs(numeric))+1e-3);
            previous=x; if(x>peak)peak=x;
        }
        if(s.zeta>=1) assert(peak<=1+1e-9); else assert(peak>1&&peak<1.2);
    }
    /* Interrupting a move: the second step starts at zero velocity, so the
       sum keeps position and velocity continuous where the target changes. */
    VCSpring s={12,.7};
    double cut=.08;
    double left=vc_spring_step(cut-1e-7,s)-vc_spring_step(cut-1e-7-cut,s);
    double right=vc_spring_step(cut+1e-7,s)-vc_spring_step(cut+1e-7-cut,s);
    assert(fabs(left-right)<1e-5);
    double vl=vc_spring_velocity(cut-1e-7,s)-vc_spring_velocity(-1e-7,s);
    double vr=vc_spring_velocity(cut+1e-7,s)-vc_spring_velocity(1e-7,s);
    assert(fabs(vl-vr)<1e-3);
    /* Monotone settle that finishes exactly on time. */
    double previous=0;
    for(int i=0;i<=1000;i++) {
        double x=vc_settle(i*.0005,.4);
        assert(x>=previous-1e-12&&x<=1); previous=x;
    }
    assert(vc_settle(.4,.4)==1&&vc_settle(0,.4)==0&&vc_settle(NAN,.4)==0);
}

static void noise(void) {
    assert(vc_noise(7,3)==0); /* gradient noise is zero on lattice points */
    double low=0, high=0, previous=vc_noise(7,0);
    for(int i=1;i<=20000;i++) {
        double t=i*.001, n=vc_noise(7,t);
        assert(n>=-1&&n<=1&&fabs(n-previous)<.01);
        previous=n; low=fmin(low,n); high=fmax(high,n);
        assert(vc_noise2(7,t)>=-1&&vc_noise2(7,t)<=1);
    }
    assert(low<-.2&&high>.2); /* it actually moves */
    assert(vc_noise(7,2.5)==vc_noise(7,2.5));
    assert(vc_noise(7,2.5)!=vc_noise(8,2.5));
    assert(vc_noise(7,NAN)==0&&vc_jitter(1,1)!=vc_jitter(2,1));
}

static void word_motion(void) {
    /* At rest before and long after a word: exact values, not approximately. */
    VCWordMotion a=vc_word_motion(0,10,10.4,.4,INFINITY,.5,99,.3,0);
    VCWordMotion b=vc_word_motion(8,10,10.4,.4,INFINITY,.5,99,.3,0);
    assert(memcmp(&a,&b,sizeof(a))==0&&a.reveal==0&&a.heat==0);
    VCWordMotion c=vc_word_motion(40,10,10.4,.4,INFINITY,.5,99,.3,0);
    VCWordMotion d=vc_word_motion(55,10,10.4,.4,INFINITY,.5,99,.3,0);
    assert(memcmp(&c,&d,sizeof(c))==0&&c.reveal==1&&c.enter==1&&c.heat==0&&c.scale==1);
    /* Continuous through the onset, the end of the word and the exit. */
    double exit_start=11.0;
    VCWordMotion previous=vc_word_motion(9.5,10,10.4,.4,exit_start,.5,99,.6,0);
    for(int i=1;i<=2400*3;i++) {
        double t=9.5+i/2400.0;
        VCWordMotion m=vc_word_motion(t,10,10.4,.4,exit_start,.5,99,.6,0);
        assert(fabs(m.reveal-previous.reveal)<.02&&fabs(m.enter-previous.enter)<.03);
        assert(fabs(m.heat-previous.heat)<.03&&fabs(m.leave-previous.leave)<.02);
        assert(fabs(m.y-previous.y)<.01&&fabs(m.scale-previous.scale)<.01);
        assert(fabs(m.angle-previous.angle)<.05&&m.heat>=0&&m.heat<=1.15);
        previous=m;
    }
    /* A held note swells further than a clipped syllable. */
    double shortPeak=0, longPeak=0;
    for(int i=0;i<300;i++) {
        shortPeak=fmax(shortPeak,vc_word_motion(10+i*.005,10,10.05,.1,INFINITY,0,5,0,0).heat);
        longPeak=fmax(longPeak,vc_word_motion(10+i*.005,10,11.5,1.5,INFINITY,0,5,0,0).heat);
    }
    assert(longPeak>shortPeak+.1);
    /* The exit runs in reading order. */
    VCWordMotion first=vc_word_motion(11.12,10,10.4,.4,11,0,99,0,0);
    VCWordMotion last=vc_word_motion(11.12,10,10.4,.4,11,1,99,0,0);
    assert(first.leave>last.leave);
    /* Seeded: identical timing, different words, different motion. */
    VCWordMotion one=vc_word_motion(10.07,10,10.9,.9,INFINITY,.5,1,0,0);
    VCWordMotion two=vc_word_motion(10.07,10,10.9,.9,INFINITY,.5,2,0,0);
    assert(one.enter!=two.enter||one.y!=two.y);
    /* Reduced motion: no gesture, no overshoot, opacity still arrives. */
    VCWordMotion calm=vc_word_motion(10.3,10,11.5,1.5,INFINITY,.5,99,1,1);
    assert(calm.x==0&&calm.y==0&&calm.angle==0&&calm.scale==1&&calm.enter==calm.reveal);
    assert(vc_rolling_handoff(-.1,.4,3,0)==0&&vc_rolling_handoff(20,.4,3,0)==1);
    /* The rolling window keeps a word until the next one has handed over
       (LyricLayerCache.rollingHandover, 3 s): every handoff has landed. */
    for(int c=0;c<=40;c++)for(uint64_t seed=0;seed<64;seed++)for(int e=0;e<=4;e++){
        double cadence=c==40?INFINITY:.02+c*.5;
        assert(vc_rolling_handoff(3,cadence,seed*0x9E3779B97F4A7C15ULL,e*.25)==1);
    }
    VCWordPose rest=vc_idle_drift(3,12.5,0);
    assert(rest.x==0&&rest.y==0&&rest.scale==1&&rest.angle==0);
    VCWordPose drift=vc_idle_drift(3,12.5,1);
    assert(fabs(drift.x)<.03&&fabs(drift.y)<.03&&fabs(drift.angle)<.5);
}

/* Music re-anchors its position every couple of seconds with some error. */
static double raw_position(double wall) {
    int anchor=(int)(wall/2);
    double error=(anchor%3-1)*.04;
    return wall+error;
}

static void clock_and_follower(void) {
    VCLyricClock c30={0}, c60={0};
    vc_lyric_clock_advance(&c30,0,raw_position(0),1);
    vc_lyric_clock_advance(&c60,0,raw_position(0),1);
    double previous=c60.time;
    for(int frame=1;frame<=600;frame++) {
        double wall=frame/60.0;
        assert(vc_lyric_clock_advance(&c60,wall,raw_position(wall),1)==0);
        double step=c60.time-previous;
        assert(step>(1/60.0)*.85&&step<(1/60.0)*1.15); /* never a visible jump */
        previous=c60.time;
        assert(vc_lyric_clock_advance(&c60,wall,raw_position(wall),1)==0); /* same frame twice */
        assert(c60.time==previous);
        if(frame%2==0) {
            vc_lyric_clock_advance(&c30,wall,raw_position(wall),1);
            assert(fabs(c30.time-c60.time)<.002); /* 30 and 60 fps agree */
        }
    }
    assert(fabs(c60.time-raw_position(10))<.05);
    /* A seek is adopted at once and reported. */
    assert(vc_lyric_clock_advance(&c60,10.02,42,1)==1&&c60.time==42);
    /* Paused: never runs backwards. */
    double held=c60.time;
    vc_lyric_clock_advance(&c60,10.1,41.9,0);
    assert(c60.time==held);

    VCFollower f30={0}, f60={0};
    vc_follow(&f30,0,0,.2,0); vc_follow(&f60,0,0,.2,0);
    for(int frame=1;frame<=120;frame++) {
        double v60=vc_follow(&f60,1,1/60.0,.2,0);
        if(frame%2==0) assert(fabs(vc_follow(&f30,1,1/30.0,.2,0)-v60)<1e-9);
    }
    VCFollower limited={0};
    vc_follow(&limited,0,0,.05,0);
    double last=0;
    for(int frame=1;frame<=120;frame++) {
        double value=vc_follow(&limited,frame%8<4?1:0,1/60.0,.05,.4);
        assert(fabs(value-last)<=.4/60.0+1e-12); /* rate cap holds against a square wave */
        last=value;
    }
}

int main(void) {
    original_curves();
    springs();
    noise();
    word_motion();
    clock_and_follower();
    puts("Lyric springs, seeded noise, word motion, exits, smoothed clock, follower, seeks and reduced motion passed.");
}
