#include "../Sources/Support/ConstellationGeometry.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
static int inside(VCBox b,double x,double y) {
    return x>b.x+1e-6&&x<b.x+b.width-1e-6&&y>b.y+1e-6&&y<b.y+b.height-1e-6;
}
int main(void) {
    /* Every rotated/growing ink corner fits the fixed graph anchor. */
    for(int angle=0;angle<=20;angle++) {
        VCBox envelope=vc_word_envelope(240,42,1.20,angle);
        for(int phase=0;phase<=100;phase++) {
            double rotation=angle*(phase/100.0)*0.017453292519943295;
            double scale=.8+.377*(phase/100.0);
            for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2) {
                double px=x*120*scale,py=y*21*scale;
                double rx=px*cos(rotation)-py*sin(rotation),ry=px*sin(rotation)+py*cos(rotation);
                assert(fabs(rx)<=envelope.width/2+1e-8);
                assert(fabs(ry)<=envelope.height/2+1e-8);
            }
        }
    }
    assert(vc_word_envelope(NAN,42,1.2,10).width==0);
    /* Arrival is monotonic during playback and recomputes correctly on seek. */
    double previous=0;
    for(int i=0;i<500;i++) {
        double value=vc_word_reveal(i/100.0,1,2);
        assert(value>=previous&&value>=0&&value<=1);previous=value;
    }
    assert(vc_word_reveal(.9,1,2)==0);
    assert(vc_word_reveal(1.1,1,2)>0&&vc_word_reveal(1.1,1,2)<1);
    assert(vc_word_reveal(3,1,2)==1);
    assert(vc_word_reveal(.5,1,2)==0); /* backwards seek */
    assert(vc_word_reveal(NAN,1,2)==0);
    assert(vc_word_visibility(.5,1,2,1,1,.5)==0);
    assert(fabs(vc_word_visibility(.75,1,2,1,1,.5)-.125)<1e-8);
    assert(vc_word_visibility(.5,1,2,1,0,.5)==0);
    assert(vc_word_visibility(.5,1,2,0,1,.5)==0);
    assert(vc_word_visibility(1,1,2,1,0,.5)==.25); /* no blink at preview handoff */
    assert(vc_word_visibility(1.1,1,2,1,0,.5)>.25);
    assert(vc_word_visibility(3,1,2,1,0,.5)==1);
    assert(vc_word_visibility(.5,1,2,1,0,.5)==0); /* backwards seek */
    for(int i=0;i<=100;i++) {
        double p=i/100.0;
        assert(vc_word_settle(p)>=0&&vc_word_settle(p)<=1.05);
    }
    VCVocalWindow vocals[]={{10,14},{22,28}};VCVocalGap gap={0};
    assert(vc_vocal_gap(vocals,2,5,40,2.4,&gap)==1&&gap.start==0&&gap.end==10&&gap.nextVocal==0);
    assert(vc_vocal_gap(vocals,2,12,40,2.4,&gap)==0);
    assert(vc_vocal_gap(vocals,2,18,40,2.4,&gap)==1&&gap.start==14&&gap.end==22&&gap.nextVocal==1);
    assert(vc_vocal_gap(vocals,2,32,40,2.4,&gap)==1&&gap.start==28&&gap.end==40&&gap.nextVocal==-1);
    assert(vc_vocal_gap(vocals,2,40,40,2.4,&gap)==0);
    VCVocalWindow breath[]={{0,5},{6,10}};
    assert(vc_vocal_gap(breath,2,5.5,20,2.4,&gap)==0);
    VCVocalWindow overlapping_vocals[]={{0,8},{4,10},{14,18}};
    assert(vc_vocal_gap(overlapping_vocals,3,6,22,2.4,&gap)==0);
    assert(vc_vocal_gap(overlapping_vocals,3,12,22,2.4,&gap)==1&&gap.start==10&&gap.end==14);
    assert(vc_vocal_gap(NULL,0,5,20,2.4,&gap)==1&&gap.nextVocal==-1);
    assert(vc_vocal_gap(vocals,2,NAN,40,2.4,&gap)==0);
    assert(vc_estimated_line_end(10,14,4,.45)==14); /* normal verse */
    assert(vc_estimated_line_end(10,50,4,.45)<17); /* long instrumental */
    assert(vc_estimated_line_end(10,30,20,.50)==30); /* a full long line */
    assert(vc_estimated_line_end(10,90,4,NAN)<17); /* safe default */
    VCBox view={0,0,1200,700};
    VCSegment links[64];
    VCBox horizontal[]={{20,100,100,40},{300,100,200,40}};
    int n=vc_build_connections(horizontal,2,view,6,links,64);
    assert(n==1);assert(fabs(links[0].ax-126)<1e-6);assert(fabs(links[0].bx-294)<1e-6);
    assert(fabs(links[0].ay-120)<1e-6);assert(fabs(links[0].by-120)<1e-6);
    VCBox vertical[]={{100,20,50,60},{100,220,50,60}};
    assert(vc_build_connections(vertical,2,view,6,links,64)==1);
    assert(fabs(links[0].ay-86)<1e-6);assert(fabs(links[0].by-214)<1e-6);
    VCBox overlap[]={{20,30,150,80},{70,50,100,80}};
    assert(vc_build_connections(overlap,2,view,6,links,64)==0);
    VCBox invalid[]={{NAN,30,50,50},{100,50,40,40}};
    assert(vc_build_connections(invalid,2,view,6,links,64)==0);
    VCBox offscreen[]={{-500,-500,30,30},{100,50,40,40}};
    assert(vc_build_connections(offscreen,2,view,6,links,64)==0);
    assert(vc_build_connections(horizontal,2,view,6,links,0)==0);
    assert(vc_build_connections(NULL,2,view,6,links,64)==0);
    VCBox words[]={{30,300,110,45},{220,80,140,50},{490,125,60,35},
                   {650,350,400,120},{140,530,160,50},{550,550,90,40}};
    n=vc_build_connections(words,6,view,8,links,64);
    assert(n>0&&n<=5);
    for(int i=0;i<n;i++){
        VCSegment s=links[i];
        assert(isfinite(s.ax)&&isfinite(s.ay)&&isfinite(s.bx)&&isfinite(s.by));
        assert(s.ax>=0&&s.ax<=1200&&s.bx>=0&&s.bx<=1200);
        assert(s.ay>=0&&s.ay<=700&&s.by>=0&&s.by<=700);
        for(int k=0;k<6;k++){
            assert(!inside(words[k],s.ax,s.ay));assert(!inside(words[k],s.bx,s.by));
            for(int t=1;t<100;t++){
                double f=t/100.0;assert(!inside(words[k],s.ax+(s.bx-s.ax)*f,s.ay+(s.by-s.ay)*f));
            }
        }
    }
    /* A word growing to its peak stays clear of every existing connection. */
    words[3].width*=1.10;words[3].height*=1.10;
    n=vc_build_connections(words,6,view,8,links,64);assert(n<=5);
    for(int i=0;i<n;i++)for(int k=0;k<6;k++)for(int t=1;t<100;t++){
        double f=t/100.0;assert(!inside(words[k],links[i].ax+(links[i].bx-links[i].ax)*f,
            links[i].ay+(links[i].by-links[i].ay)*f));
    }
    /* Lighting/arrival redraws reuse geometry; only actual layout changes
       rebuild it. Each canvas owns a bounded, independent cache. */
    VCConnectionCache memo={0};VCSegment cached[64];
    for(int frame=0;frame<1000;frame++) {
        int found=vc_cached_connections(&memo,words,6,view,8,cached,64);
        assert(found==n&&memo.builds==1);
        for(int j=0;j<found;j++) {
            assert(cached[j].from==links[j].from&&cached[j].to==links[j].to);
            assert(cached[j].ax==links[j].ax&&cached[j].ay==links[j].ay);
            assert(cached[j].bx==links[j].bx&&cached[j].by==links[j].by);
        }
    }
    assert(n>1);
    assert(vc_cached_connections(&memo,words,6,view,8,cached,1)==1&&memo.builds==1);
    assert(vc_cached_connections(&memo,words,6,view,8,cached,64)==n&&memo.builds==1);
    VCBox resized=view;resized.width=1000;
    vc_cached_connections(&memo,words,6,resized,8,cached,64);assert(memo.builds==2);
    words[0].width+=10;
    vc_cached_connections(&memo,words,6,resized,8,cached,64);assert(memo.builds==3);
    vc_cached_connections(&memo,words,6,resized,9,cached,64);assert(memo.builds==4);
    vc_cached_connections(&memo,words,5,resized,9,cached,64);assert(memo.builds==5);
    VCConnectionCache second_display={0};
    vc_cached_connections(&second_display,horizontal,2,view,6,cached,64);
    assert(second_display.builds==1&&memo.builds==5);
    VCConnectionCache empty={0};
    for(int frame=0;frame<10;frame++)assert(vc_cached_connections(&empty,overlap,2,view,6,cached,64)==0);
    assert(empty.builds==1); /* Empty results are cached too. */
    assert(vc_cached_connections(NULL,words,6,view,8,cached,64)==0);
    assert(vc_cached_connections(&memo,words,65,view,8,cached,64)==0);
    assert(vc_cached_connections(&memo,words,6,view,8,cached,0)==0);
    /* Word timing wins when the recording metadata agrees; a wrong artist never wins. */
    assert(vc_match_score(1,1,1,1,1,1,1)>vc_match_score(1,1,1,1,0,1,1));
    assert(vc_match_score(1,.2,0,1,1,1,1)<0);
    assert(vc_match_score(1,1,80,1,1,1,1)<0);
    assert(vc_match_score(NAN,1,0,1,1,1,1)<0);
    puts("Geometry and match-ranking invariants passed.");
    return 0;
}
