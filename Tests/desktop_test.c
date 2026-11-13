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

    /* A held button never starts a reveal (rubber-band selection, file drags)
       and cannot carry one to another display; an active reveal survives a
       press so the scrubber can be dragged. */
    VCHoverSession press={0};
    assert(vc_hover_step(&press,screens,5,laptop,0,1,30,.7)==0);
    assert(press.activeDisplayID==0);
    assert(vc_hover_step(&press,screens,5,laptop,0,0,30.1,.7)==101);
    assert(vc_hover_step(&press,screens,5,laptop,0,1,30.2,.7)==101);
    assert(vc_hover_step(&press,screens,5,(VCPoint){50,50},0,1,30.4,.7)==101); /* linger while held */
    assert(vc_hover_step(&press,screens,5,right,0,1,30.5,.7)==0); /* no hand-off mid-drag */
    assert(vc_hover_step(&press,screens,5,right,0,1,30.6,.7)==0);
    assert(vc_hover_step(&press,screens,5,right,0,0,30.7,.7)==202);
    assert(vc_hover_step(&press,screens,5,right,1,1,30.8,.7)==0); /* occlusion still wins */
    assert(vc_hover_step(NULL,screens,5,right,0,0,31,.7)==0);

    /* Coverage: exact union area and the "no noticeable exposed square" rule. */
    VCBox visible={0,25,1512,945};
    VCBox maximised[]={{0,25,1512,945}};
    assert(vc_exposed_fraction(visible,maximised,1)==0);
    assert(vc_region_covered(visible,maximised,1,24,.06)==1);
    assert(vc_exposed_fraction(visible,NULL,0)==1);
    assert(vc_region_covered(visible,NULL,0,24,.06)==0);
    VCBox half[]={{0,25,756,945}};
    assert(fabs(vc_exposed_fraction(visible,half,1)-.5)<1e-9);
    assert(vc_region_covered(visible,half,1,24,.06)==0);
    /* Overlapping windows are not double counted. */
    VCBox overlap[]={{0,25,1000,945},{500,25,1012,945}};
    assert(vc_exposed_fraction(visible,overlap,2)==0);
    assert(vc_region_covered(visible,overlap,2,24,.06)==1);
    /* Tiled windows with 8 pt margins leave only slivers: still covered,
       unless the caller treats an 8 pt strip as noticeable. */
    VCBox tiled[]={{8,33,744,929},{760,33,744,929}};
    double sliver=vc_exposed_fraction(visible,tiled,2);
    assert(sliver>.02&&sliver<.04);
    assert(vc_region_covered(visible,tiled,2,24,.06)==1);
    assert(vc_region_covered(visible,tiled,2,4,.06)==0);
    assert(vc_region_covered(visible,tiled,2,24,.01)==0); /* area budget */
    /* A 100 pt hole in the middle (where lyrics sit) is visible desktop. */
    VCBox frame[]={{0,25,1512,422},{0,547,1512,423},{0,447,706,100},{806,447,706,100}};
    assert(vc_exposed_fraction(visible,frame,4)>0);
    assert(vc_region_covered(visible,frame,4,24,.06)==0);
    /* Windows spilling onto another display are clipped to this one. */
    VCBox spill[]={{-400,0,2400,1200}};
    assert(vc_region_covered(visible,spill,1,24,.06)==1);
    VCBox elsewhere[]={{1600,0,800,600}};
    assert(vc_exposed_fraction(visible,elsewhere,1)==1);
    /* Negative/stacked origins and malformed input. */
    VCBox left_display={-1920,-200,1920,1080};
    VCBox left_cover[]={{-1925,-205,1930,1090}};
    assert(vc_region_covered(left_display,left_cover,1,24,.06)==1);
    VCBox junk[]={{NAN,0,100,100},{0,0,-5,10},{0,25,1512,945}};
    assert(vc_region_covered(visible,junk,3,24,.06)==1);
    assert(vc_region_covered((VCBox){NAN,0,10,10},maximised,1,24,.06)==0);
    assert(vc_region_covered(visible,maximised,1,NAN,.06)==0);
    assert(vc_exposed_fraction((VCBox){0,0,0,10},maximised,1)==1);
    /* Only the first VC_MAX_COVERS covers count, so a long list is bounded
       and can only under-report coverage. */
    VCBox many[VC_MAX_COVERS+8];
    for(int i=0;i<VC_MAX_COVERS+8;i++)many[i]=(VCBox){0,0,1,1};
    many[VC_MAX_COVERS+2]=(VCBox){0,25,1512,945};
    assert(vc_region_covered(visible,many,VC_MAX_COVERS+8,24,.06)==0);
    many[0]=(VCBox){0,25,1512,945};
    assert(vc_region_covered(visible,many,VC_MAX_COVERS+8,24,.06)==1);
    /* With the card's size set, the zones hug the card. */
    vc_hover_set_card(640,216);
    {   VCBox big=(VCBox){0,0,2560,1440},hot=vc_hover_hot_zone(big),keep=vc_hover_keep_zone(big);
        assert(hot.width==640+2*VC_HOVER_REVEAL_MARGIN&&hot.height==216+2*VC_HOVER_REVEAL_MARGIN);
        assert(hot.x==(2560-hot.width)/2&&hot.y==(1440-hot.height)/2);
        assert(keep.width==640+2*VC_HOVER_KEEP_MARGIN&&keep.width<vc_control_panel_frame(big).width&&keep.height<vc_control_panel_frame(big).height);
        VCBox tiny=(VCBox){0,0,500,300};
        assert(vc_hover_hot_zone(tiny).width==500); /* never past the screen */
    }
    vc_hover_set_card(0,0);
    assert(vc_hover_keep_zone((VCBox){0,0,2560,1440}).width==780); /* unset: as before */
    puts("Held-button hover gating and window-coverage geometry passed.");
}
