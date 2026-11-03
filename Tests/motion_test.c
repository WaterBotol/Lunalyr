#include "../Sources/Support/LyricMotion.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

int main(void) {
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
    }
    VCWordPose settled=vc_vocal_gesture(30,.4,101,.7,0);
    assert(settled.x==0&&settled.y==0&&settled.scale==1);
    VCWordPose first=vc_vocal_gesture(.7,2,101,.3,0);
    VCWordPose held=vc_vocal_gesture(1.2,2,101,.3,0);
    assert(first.y!=held.y);
    VCWordPose invalid=vc_rolling_pose(0,NAN,INFINITY,NAN,0,NAN,0);
    assert(isfinite(invalid.x)&&isfinite(invalid.y)&&isfinite(invalid.opacity));
    puts("Lyric handoffs, free-stage exits, live energy, held notes, seeks and reduced motion passed.");
}
