#include "DesktopInteraction.h"
#include <math.h>

static int valid(VCBox b) {
    return isfinite(b.x)&&isfinite(b.y)&&isfinite(b.width)&&isfinite(b.height)&&b.width>0&&b.height>0;
}
static int contains(VCBox b,VCPoint p) {
    return valid(b)&&isfinite(p.x)&&isfinite(p.y)&&p.x>=b.x&&p.y>=b.y&&p.x<b.x+b.width&&p.y<b.y+b.height;
}
VCBox vc_hover_hot_zone(VCBox s) {
    if(!valid(s))return (VCBox){0};
    return (VCBox){s.x+s.width*.35,s.y+s.height*.35,s.width*.30,s.height*.30};
}
VCBox vc_control_panel_frame(VCBox s) {
    if(!valid(s))return (VCBox){0};
    double width=fmin(400,s.width),height=fmin(330,s.height);
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
    int controls=s->activeDisplayID==display&&contains(vc_control_panel_frame(screens[index].frame),mouse);
    if(hot||controls) {
        s->activeDisplayID=display;
        s->deadline=now+(isfinite(linger)?fmax(0,fmin(2,linger)):.7);
    } else if(now>=s->deadline) {s->activeDisplayID=0;}
    return s->activeDisplayID;
}
