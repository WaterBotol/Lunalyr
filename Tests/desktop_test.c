#include "../Sources/Support/DesktopInteraction.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>

int main(void) {
    VCDisplayFrame screens[]={
        {101,{0,0,1440,900}}, {202,{1440,0,2560,1440}},
        {303,{-1920,0,1920,1080}}, {404,{0,900,1600,1200}},
        {505,{100,-1200,1600,1200}}
    };
    VCPoint laptop={720,450},right={2720,720},left={-960,540},above={800,1500},below={900,-600};
    assert(vc_display_at_point(screens,5,laptop)==0);
    assert(vc_display_at_point(screens,5,right)==1);
    assert(vc_display_at_point(screens,5,left)==2);
    assert(vc_display_at_point(screens,5,above)==3);
    assert(vc_display_at_point(screens,5,below)==4);
    assert(vc_display_at_point(screens,5,(VCPoint){1440,100})==1); /* shared edge, one display */
    assert(vc_display_at_point(screens,5,(VCPoint){5000,5000})==-1);
    assert(vc_display_at_point(screens,5,(VCPoint){NAN,200})==-1);
    assert(vc_display_at_point(NULL,5,laptop)==-1);
    for(int i=0;i<5;i++) {
        VCBox f=screens[i].frame,panel=vc_control_panel_frame(f),hot=vc_hover_hot_zone(f);
        assert(fabs(panel.x+panel.width/2-(f.x+f.width/2))<1e-8);
        assert(fabs(panel.y+panel.height/2-(f.y+f.height/2))<1e-8);
        assert(fabs(hot.x+hot.width/2-(f.x+f.width/2))<1e-8);
        assert(fabs(hot.y+hot.height/2-(f.y+f.height/2))<1e-8);
    }
    assert(vc_control_panel_frame((VCBox){0,0,200,150}).width==200);
    assert(vc_hover_hot_zone((VCBox){NAN,0,300,200}).width==0);

    VCHoverSession state={0};
    assert(vc_hover_update(&state,screens,5,laptop,0,10,.7)==101);
    assert(vc_hover_update(&state,screens,5,right,0,10.01,.7)==202); /* no laptop blur */
    assert(state.activeDisplayID!=101);
    assert(vc_hover_update(&state,screens,5,laptop,1,10.02,.7)==0); /* app covers cursor */
    assert(vc_hover_update(&state,screens,5,laptop,0,11,.7)==101);
    assert(vc_hover_update(&state,screens,5,(VCPoint){1450,100},0,11.01,.7)==0); /* another display, no linger */
    assert(vc_hover_update(&state,screens,5,laptop,0,12,.7)==101);
    assert(vc_hover_update(&state,screens,5,(VCPoint){50,50},0,12.3,.7)==101); /* same-display linger */
    assert(vc_hover_update(&state,screens,5,(VCPoint){50,50},0,12.71,.7)==0);
    assert(vc_hover_update(&state,screens,5,laptop,0,13,.7)==101);
    assert(vc_hover_update(&state,screens,5,(VCPoint){50,50},1,13.01,.7)==0); /* occlusion cancels linger */
    assert(vc_hover_update(&state,screens,5,left,0,14,.7)==303);
    assert(vc_hover_update(&state,screens,5,above,0,15,.7)==404);
    assert(vc_hover_update(&state,screens,1,above,0,15.01,.7)==0); /* unplugged display */
    assert(vc_hover_update(&state,screens,5,laptop,0,NAN,.7)==0);

    /* A short screen's controls extend outside the reveal zone. They must
       stay usable after the pointer leaves the original small hot zone. */
    VCDisplayFrame small={606,{0,0,800,600}};
    assert(vc_hover_update(&state,&small,1,(VCPoint){400,300},0,20,.7)==606);
    assert(vc_hover_update(&state,&small,1,(VCPoint){400,160},0,21,.7)==606);

    /* Only the hit target matters. A non-interactive helper on another
       display is never supplied to this classifier by AppKit's hit test. */
    const int32_t desktop=-2147483603;
    assert(vc_hit_exposes_desktop(0,0,0,0,0)==1); /* bare desktop */
    assert(vc_hit_exposes_desktop(44,0,0,0,1)==1); /* own wallpaper */
    assert(vc_hit_exposes_desktop(55,0,0,0,1)==1); /* own controls */
    assert(vc_hit_exposes_desktop(66,1,desktop,0,0)==1); /* Finder desktop */
    assert(vc_hit_exposes_desktop(66,1,-1,0,0)==1); /* background desktop surface */
    assert(vc_hit_exposes_desktop(77,1,0,0,0)==0); /* normal app */
    assert(vc_hit_exposes_desktop(77,1,25,0,0)==0); /* menu / floating app */
    assert(vc_hit_exposes_desktop(77,0,0,0,0)==0); /* unknown interactive target */
    puts("Display isolation, point-hit classification, negative/stacked coordinates, linger and unplugging passed.");
}
