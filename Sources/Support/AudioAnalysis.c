#include "AudioAnalysis.h"
#include <math.h>
#include <stdint.h>
#include <string.h>

static double follow(double previous,double target,double dt) {
    double tau=target>previous ? .035 : .22;
    return previous+(target-previous)*(1-exp(-dt/tau));
}
static int sample_size(VCPCMFormat f) {
    switch(f) {
    case VC_PCM_FLOAT32:case VC_PCM_INT32:return 4;
    case VC_PCM_FLOAT64:return 8;
    case VC_PCM_INT16:return 2;
    case VC_PCM_INT24:return 3;
    default:return 0;
    }
}
static double sample(const uint8_t *p,VCPCMFormat f) {
    switch(f) {
    case VC_PCM_FLOAT32:{float x;memcpy(&x,p,4);return x;}
    case VC_PCM_FLOAT64:{double x;memcpy(&x,p,8);return x;}
    case VC_PCM_INT16:{int16_t x;memcpy(&x,p,2);return x/32768.0;}
    case VC_PCM_INT32:{int32_t x;memcpy(&x,p,4);return x/2147483648.0;}
    case VC_PCM_INT24:{uint32_t u=(uint32_t)p[0]|((uint32_t)p[1]<<8)|((uint32_t)p[2]<<16);
        int32_t x=(int32_t)(u&0x800000 ? u|0xff000000 : u);return x/8388608.0;}
    default:return 0;
    }
}
VCAudioLevels vc_audio_process_pcm(VCAudioState *s,const void *data,int bytes,int frames,int stride,VCPCMFormat format,double rate) {
    int width=sample_size(format);
    if(!s||!data||frames<=0||frames>192000||width==0||stride<width||stride>512||bytes<width||
       (int64_t)(frames-1)*stride+width>bytes||!isfinite(rate)||rate<8000||rate>192000)
        return (VCAudioLevels){0};
    const uint8_t *pcm=data;
    const double low_a=1-exp(-6.283185307179586*200/rate);
    const double upper_a=1-exp(-6.283185307179586*2500/rate);
    double energy=0,bass=0,mid=0,treble=0;
    for(int i=0;i<frames;i++) {
        double x=sample(pcm+(size_t)i*stride,format);
        if(!isfinite(x))x=0;
        x=fmax(-1,fmin(1,x));
        s->low+=low_a*(x-s->low);
        s->upper+=upper_a*(x-s->upper);
        double m=s->upper-s->low,t=x-s->upper;
        energy+=x*x;bass+=s->low*s->low;mid+=m*m;treble+=t*t;
    }
    double dt=(double)frames/rate,raw=sqrt(energy/frames);
    s->rms=follow(s->rms,raw,dt);
    s->bass=follow(s->bass,sqrt(bass/frames),dt);
    s->mid=follow(s->mid,sqrt(mid/frames),dt);
    s->treble=follow(s->treble,sqrt(treble/frames),dt);
    double transient=fmax(0,(raw-s->average)/fmax(.01,s->average));
    s->since_beat+=dt;s->pulse*=exp(-dt/.19);
    if(raw>.0015&&transient>.40&&(s->since_beat>.17||s->average<.001)) {
        s->pulse=fmin(1,transient*.65);s->since_beat=0;
    }
    s->average+=(raw-s->average)*(1-exp(-dt/.45));
    return (VCAudioLevels){s->rms,s->bass,s->mid,s->treble,s->pulse};
}
VCAudioLevels vc_audio_process(VCAudioState *s,const float *pcm,int frames,int stride,double rate) {
    if(frames<=0||frames>192000||stride<1||stride>32)return (VCAudioLevels){0};
    return vc_audio_process_pcm(s,pcm,((frames-1)*stride+1)*4,frames,stride*4,VC_PCM_FLOAT32,rate);
}

int vc_audio_decode_pcm(const void *data,int bytes,int frames,int stride,VCPCMFormat format,float *output,int capacity) {
    int width=sample_size(format);
    if(!data||!output||frames<=0||frames>192000||capacity<frames||width==0||stride<width||stride>512||
       (int64_t)(frames-1)*stride+width>bytes)return 0;
    const uint8_t *pcm=data;
    for(int i=0;i<frames;i++) {
        double x=sample(pcm+(size_t)i*stride,format);
        output[i]=(float)(isfinite(x)?fmax(-1,fmin(1,x)):0);
    }
    return frames;
}
