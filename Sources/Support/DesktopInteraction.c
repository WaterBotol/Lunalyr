#include "DesktopInteraction.h"
#include <math.h>
#include <stdlib.h>

static int valid(VCBox b) {
    return isfinite(b.x)&&isfinite(b.y)&&isfinite(b.width)&&isfinite(b.height)&&b.width>0&&b.height>0;
}
static int contains(VCBox b,VCPoint p) {
    return valid(b)&&isfinite(p.x)&&isfinite(p.y)&&p.x>=b.x&&p.y>=b.y&&p.x<b.x+b.width&&p.y<b.y+b.height;
}
static double card_w=0,card_h=0;
void vc_hover_set_card(double width,double height) {
    card_w=(isfinite(width)&&width>0)?width:0;
    card_h=(isfinite(height)&&height>0)?height:0;
}
/* The card centred on the screen, grown by `margin`, kept on the screen. */
static VCBox card_zone(VCBox s,double margin) {
    double w=fmin(s.width,card_w+2*margin),h=fmin(s.height,card_h+2*margin);
    return (VCBox){s.x+(s.width-w)/2,s.y+(s.height-h)/2,w,h};
}
VCBox vc_hover_hot_zone(VCBox s) {
    if(!valid(s))return (VCBox){0};
    if(card_w>0&&card_h>0)return card_zone(s,VC_HOVER_REVEAL_MARGIN);
    return (VCBox){s.x+s.width*.35,s.y+s.height*.35,s.width*.30,s.height*.30};
}
VCBox vc_hover_keep_zone(VCBox s) {
    if(!valid(s))return (VCBox){0};
    if(card_w>0&&card_h>0)return card_zone(s,VC_HOVER_KEEP_MARGIN);
    return vc_control_panel_frame(s);
}
VCBox vc_control_panel_frame(VCBox s) {
    if(!valid(s))return (VCBox){0};
    double width=fmin(780,s.width),height=fmin(780,s.height);
    return (VCBox){s.x+(s.width-width)/2,s.y+(s.height-height)/2,width,height};
}
int vc_display_at_point(const VCDisplayFrame *screens,int count,VCPoint point) {
    if(!screens||count<1||count>256)return -1;
    for(int i=0;i<count;i++)if(screens[i].displayID&&contains(screens[i].frame,point))return i;
    return -1;
}
int vc_hit_exposes_desktop(uint32_t id,int known,int32_t layer,int32_t normal_layer,int own) {
    if(id==0||own)return 1;
    if(!known)return 0;
    return layer<normal_layer;
}
uint32_t vc_hover_update(VCHoverSession *s,const VCDisplayFrame *screens,int count,
                         VCPoint mouse,int blocked,double now,double linger) {
    if(!s)return 0;
    int index=vc_display_at_point(screens,count,mouse);
    if(index<0||blocked||!isfinite(now)) {s->activeDisplayID=0;s->deadline=0;return 0;}
    uint32_t display=screens[index].displayID;
    if(s->activeDisplayID!=display) {s->activeDisplayID=0;s->deadline=0;}
    int hot=contains(vc_hover_hot_zone(screens[index].frame),mouse);
    int controls=s->activeDisplayID==display&&contains(vc_hover_keep_zone(screens[index].frame),mouse);
    if(hot||controls) {
        s->activeDisplayID=display;
        s->deadline=now+(isfinite(linger)?fmax(0,fmin(2,linger)):.7);
    } else if(now>=s->deadline) {s->activeDisplayID=0;}
    return s->activeDisplayID;
}
uint32_t vc_hover_step(VCHoverSession *s,const VCDisplayFrame *screens,int count,
                       VCPoint mouse,int blocked,int held,double now,double linger) {
    if(!s)return 0;
    if(held) {
        /* A press may keep an existing reveal on its own display, never start one. */
        int index=vc_display_at_point(screens,count,mouse);
        if(index<0||s->activeDisplayID==0||screens[index].displayID!=s->activeDisplayID) {
            s->activeDisplayID=0;s->deadline=0;return 0;
        }
    }
    return vc_hover_update(s,screens,count,mouse,blocked,now,linger);
}

static int intersect(VCBox a,VCBox b,VCBox *out) {
    if(!valid(a)||!valid(b))return 0;
    double x0=fmax(a.x,b.x),y0=fmax(a.y,b.y);
    double x1=fmin(a.x+a.width,b.x+b.width),y1=fmin(a.y+a.height,b.y+b.height);
    if(!(x1>x0&&y1>y0))return 0;
    *out=(VCBox){x0,y0,x1-x0,y1-y0};
    return 1;
}
static int ascending(const void *a,const void *b) {
    double x=*(const double *)a,y=*(const double *)b;
    return (x>y)-(x<y);
}
/* Exact union area by coordinate compression: every compressed cell is either
   wholly inside a cover or wholly outside all of them, so its centre decides. */
double vc_exposed_fraction(VCBox region,const VCBox *covers,int count) {
    if(!valid(region))return 1;
    if(!covers||count<=0)return 1;
    if(count>VC_MAX_COVERS)count=VC_MAX_COVERS;
    VCBox clipped[VC_MAX_COVERS];
    double xs[2*VC_MAX_COVERS+2],ys[2*VC_MAX_COVERS+2];
    int n=0,nx=0,ny=0;
    xs[nx++]=region.x;xs[nx++]=region.x+region.width;
    ys[ny++]=region.y;ys[ny++]=region.y+region.height;
    for(int i=0;i<count;i++) {
        if(!intersect(region,covers[i],&clipped[n]))continue;
        xs[nx++]=clipped[n].x;xs[nx++]=clipped[n].x+clipped[n].width;
        ys[ny++]=clipped[n].y;ys[ny++]=clipped[n].y+clipped[n].height;
        n++;
    }
    if(n==0)return 1;
    qsort(xs,(size_t)nx,sizeof xs[0],ascending);
    qsort(ys,(size_t)ny,sizeof ys[0],ascending);
    double exposed=0;
    for(int i=0;i+1<nx;i++) {
        double w=xs[i+1]-xs[i];
        if(!(w>0))continue;
        VCPoint centre={xs[i]+w/2,0};
        for(int j=0;j+1<ny;j++) {
            double h=ys[j+1]-ys[j];
            if(!(h>0))continue;
            centre.y=ys[j]+h/2;
            int covered=0;
            for(int k=0;k<n&&!covered;k++)covered=contains(clipped[k],centre);
            if(!covered)exposed+=w*h;
        }
    }
    return fmin(1,fmax(0,exposed/(region.width*region.height)));
}
int vc_region_covered(VCBox region,const VCBox *covers,int count,double gap,double max_exposed) {
    if(!valid(region)||!covers||count<=0||!isfinite(gap)||!isfinite(max_exposed))return 0;
    if(count>VC_MAX_COVERS)count=VC_MAX_COVERS;
    if(vc_exposed_fraction(region,covers,count)>fmax(0,max_exposed))return 0;
    gap=fmax(0,gap);
    /* Lower-left corners of every gap-sized square that fits inside the region. */
    VCBox origins={region.x,region.y,region.width-gap,region.height-gap};
    if(!(origins.width>0&&origins.height>0))return 1;
    /* A square at p overlaps a cover exactly when p lies in the cover grown
       by the gap towards the origin. A corner outside every grown cover is
       the corner of a fully exposed square. */
    VCBox grown[VC_MAX_COVERS];
    int n=0;
    for(int i=0;i<count;i++) {
        if(!valid(covers[i]))continue;
        grown[n++]=(VCBox){covers[i].x-gap,covers[i].y-gap,covers[i].width+gap,covers[i].height+gap};
    }
    return n>0&&vc_exposed_fraction(origins,grown,n)<=1e-9;
}
