#include "ConstellationGeometry.h"
#include <math.h>
#include <string.h>
int vc_vocal_gap(const VCVocalWindow *v,int count,double time,double duration,double minimum,VCVocalGap *out) {
    if(!out||count<0||count>20000||(!v&&count)||!isfinite(time)||time<0)return 0;
    if(!isfinite(minimum))minimum=2.4;
    minimum=fmax(.5,fmin(10,minimum));
    int known_duration=isfinite(duration)&&duration>0;
    if(known_duration&&time>=duration)return 0;
    double previous=0;
    for(int i=0;i<count;i++) {
        if(!isfinite(v[i].start)||!isfinite(v[i].end)||v[i].start<0||v[i].end<v[i].start||
           (i>0&&v[i].start<v[i-1].start))return 0;
    }
    for(int i=0;i<count;i++) {
        double start=v[i].start,end=v[i].end;
        if(time>=previous&&time<start&&start-previous>=minimum) {
            *out=(VCVocalGap){previous,start,i};return 1;
        }
        while(i+1<count&&v[i+1].start<=end) {i++;end=fmax(end,v[i].end);}
        if(time>=start&&time<end)return 0;
        previous=fmax(previous,end);
    }
    if(time>=previous&&(!known_duration||duration-previous>=minimum)) {
        *out=(VCVocalGap){previous,known_duration?duration:-1,-1};return 1;
    }
    return 0;
}
VCBox vc_word_envelope(double width,double height,double peak,double degrees) {
    if(!isfinite(width)||!isfinite(height)||width<0||height<0) return (VCBox){0};
    if(!isfinite(peak))peak=1.2;
    if(!isfinite(degrees))degrees=0;
    peak=fmax(1,fmin(peak,2));
    double angle=fmin(90,fabs(degrees))*0.017453292519943295;
    double s=sin(angle);
    // Includes every orientation up to the supplied angle, not just the
    // end rotation. Entrance rotation can therefore never change the slot.
    return (VCBox){0,0,(width+height*s)*peak,(height+width*s)*peak};
}
double vc_word_reveal(double time,double start,double end) {
    if(!isfinite(time)||!isfinite(start)||!isfinite(end)||time<start)return 0;
    double duration=fmin(.46,fmax(.16,(end-start)*.65));
    double t=fmin(1,fmax(0,(time-start)/duration));
    return t*t*(3-2*t);
}
double vc_word_visibility(double time,double start,double end,int preview,int next) {
    if(!isfinite(time)||!isfinite(start)||!isfinite(end))return 0;
    if(time<start)return preview&&next ? .25 : 0;
    double progress=vc_word_reveal(time,start,end);
    return preview ? .25+.75*progress : progress;
}
double vc_word_settle(double progress) {
    if(!isfinite(progress)||progress<=0)return 0;
    if(progress>=1)return 1;
    double t=progress-1;
    return 1+1.85*t*t*t+.85*t*t;
}
double vc_estimated_line_end(double start,double next,int words,double per_word) {
    if(!isfinite(start)||!isfinite(next)||next<=start||words<1||words>10000)return next;
    if(!isfinite(per_word))per_word=.45;
    per_word=fmax(.18,fmin(1.2,per_word));
    double sung=fmax(4,fmin(22,words*per_word*1.35+1.25));
    return next-start>fmax(12,sung+6) ? start+sung : next;
}
#include <stdlib.h>

typedef struct { VCSegment segment; double cost; } VCEdge;
static int finite_box(VCBox b) {
    return isfinite(b.x) && isfinite(b.y) && isfinite(b.width) && isfinite(b.height) &&
        b.width > 0 && b.height > 0 && fabs(b.x) < 1e8 && fabs(b.y) < 1e8 &&
        b.width < 1e8 && b.height < 1e8;
}
static VCBox padded(VCBox b, double p) {
    return (VCBox){b.x-p,b.y-p,b.width+2*p,b.height+2*p};
}
static int overlaps(VCBox a, VCBox b) {
    return a.x < b.x+b.width && a.x+a.width > b.x &&
        a.y < b.y+b.height && a.y+a.height > b.y;
}
static int interior(VCBox b,double x,double y) {
    const double e=1e-7;
    return x>b.x+e && x<b.x+b.width-e && y>b.y+e && y<b.y+b.height-e;
}
/* Liang-Barsky clipping; also used to reject links through another word. */
static int clip(VCSegment *s, VCBox r) {
    double dx=s->bx-s->ax,dy=s->by-s->ay,t0=0,t1=1;
    double p[4]={-dx,dx,-dy,dy};
    double q[4]={s->ax-r.x,r.x+r.width-s->ax,s->ay-r.y,r.y+r.height-s->ay};
    for (int i=0;i<4;i++) {
        if (fabs(p[i])<1e-12) {if (q[i]<0) return 0;continue;}
        double t=q[i]/p[i];
        if (p[i]<0) {if(t>t1)return 0;if(t>t0)t0=t;}
        else {if(t<t0)return 0;if(t<t1)t1=t;}
    }
    double x=s->ax,y=s->ay;
    s->ax=x+t0*dx;s->ay=y+t0*dy;s->bx=x+t1*dx;s->by=y+t1*dy;
    return hypot(s->bx-s->ax,s->by-s->ay)>1;
}
static int connect(VCBox a,VCBox b,VCBox view,double gap,VCSegment *s) {
    VCBox pa=padded(a,gap),pb=padded(b,gap);
    if (overlaps(pa,pb)) return 0;
    double ax=a.x+a.width/2,ay=a.y+a.height/2;
    double bx=b.x+b.width/2,by=b.y+b.height/2;
    double dx=bx-ax,dy=by-ay;
    if (hypot(dx,dy)<1e-8) return 0;
    double at=fmin(fabs(dx)>1e-12?(pa.width/2)/fabs(dx):INFINITY,
                   fabs(dy)>1e-12?(pa.height/2)/fabs(dy):INFINITY);
    double bt=fmin(fabs(dx)>1e-12?(pb.width/2)/fabs(dx):INFINITY,
                   fabs(dy)>1e-12?(pb.height/2)/fabs(dy):INFINITY);
    s->ax=ax+dx*at;s->ay=ay+dy*at;s->bx=bx-dx*bt;s->by=by-dy*bt;
    if (!clip(s,view)) return 0;
    return !interior(pa,s->ax,s->ay) && !interior(pa,s->bx,s->by) &&
           !interior(pb,s->ax,s->ay) && !interior(pb,s->bx,s->by);
}
static int compare_edges(const void *aa,const void *bb) {
    const VCEdge *a=aa,*b=bb;
    if(a->cost<b->cost)return -1;
    if(a->cost>b->cost)return 1;
    if(a->segment.from!=b->segment.from)return a->segment.from-b->segment.from;
    return a->segment.to-b->segment.to;
}
static int root(int *parent,int x) {
    while(parent[x]!=x){parent[x]=parent[parent[x]];x=parent[x];}return x;
}
int vc_build_connections(const VCBox *boxes,int count,VCBox viewport,
                         double clearance,VCSegment *output,int capacity) {
    if(!boxes || !output || count<2 || count>64 || capacity<1 || !finite_box(viewport))return 0;
    double gap=isfinite(clearance)?fmax(0,fmin(clearance,40)):6;
    VCEdge edges[2016];int edge_count=0,parent[64];
    for(int i=0;i<count;i++)parent[i]=i;
    for(int i=0;i<count;i++) {
        if(!finite_box(boxes[i]) || !overlaps(boxes[i],viewport))continue;
        for(int j=i+1;j<count;j++) {
            if(!finite_box(boxes[j]) || !overlaps(boxes[j],viewport))continue;
            VCSegment line={0};line.from=i;line.to=j;
            if(!connect(boxes[i],boxes[j],viewport,gap,&line))continue;
            int blocked=0;
            for(int k=0;k<count;k++) {
                if(k==i || k==j || !finite_box(boxes[k]))continue;
                VCSegment test=line;
                if(clip(&test,padded(boxes[k],gap*.6))){blocked=1;break;}
            }
            if(blocked)continue;
            double dx=(boxes[j].x+boxes[j].width/2)-(boxes[i].x+boxes[i].width/2);
            double dy=(boxes[j].y+boxes[j].height/2)-(boxes[i].y+boxes[i].height/2);
            edges[edge_count++]=(VCEdge){line,dx*dx+dy*dy};
        }
    }
    qsort(edges,(size_t)edge_count,sizeof(VCEdge),compare_edges);
    int written=0;
    for(int i=0;i<edge_count && written<capacity;i++) {
        int a=root(parent,edges[i].segment.from),b=root(parent,edges[i].segment.to);
        if(a==b)continue;
        parent[a]=b;output[written++]=edges[i].segment;
    }
    return written;
}
static int equal_box(VCBox a,VCBox b) {
    return a.x==b.x&&a.y==b.y&&a.width==b.width&&a.height==b.height;
}
int vc_cached_connections(VCConnectionCache *cache,const VCBox *boxes,int count,
                         VCBox viewport,double clearance,VCSegment *output,int capacity) {
    if(!cache||!boxes||!output||count<2||count>64||capacity<1||!finite_box(viewport))return 0;
    double gap=isfinite(clearance)?fmax(0,fmin(clearance,40)):6;
    int hit=cache->valid&&cache->count==count&&cache->clearance==gap&&equal_box(cache->viewport,viewport);
    if(hit)for(int i=0;i<count;i++)if(!equal_box(cache->boxes[i],boxes[i])){hit=0;break;}
    if(!hit) {
        cache->written=vc_build_connections(boxes,count,viewport,gap,cache->segments,63);
        memcpy(cache->boxes,boxes,(size_t)count*sizeof(VCBox));
        cache->viewport=viewport;cache->clearance=gap;cache->count=count;cache->valid=1;
        cache->builds++;
    }
    int n=cache->written<capacity?cache->written:capacity;
    memcpy(output,cache->segments,(size_t)n*sizeof(VCSegment));
    return n;
}
double vc_match_score(double title,double artist,double delta,int known,int words,int lines,int album) {
    if(!isfinite(title)||!isfinite(artist)||!isfinite(delta))return -1000;
    if(title<.62 || artist<.45)return -1000;
    if(known && delta>fmax(25,0))return -1000;
    double duration=known?fmax(-22,14-delta*2):0;
    return title*48+artist*27+duration+(words?20:lines?7:0)+(album?4:0);
}
