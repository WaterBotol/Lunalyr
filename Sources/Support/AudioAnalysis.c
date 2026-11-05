#include "AudioAnalysis.h"
#include <math.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdlib.h>
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

/* Lock-free SPSC ring. The producer owns write_index and the consumer owns
   read_index; each publishes its index with release ordering after touching
   the samples, and reads the other side's index with acquire ordering. The
   indices only increase (64-bit frame counts never wrap in practice). */
struct VCAudioRing {
    float *samples; /* capacity interleaved stereo frames */
    uint32_t capacity, mask;
    _Atomic uint64_t write_index, read_index;
    _Atomic uint64_t packets, dropped, unsupported;
    _Atomic int channels, format;
    _Atomic uint64_t rate_bits;
};
static int view_fits(int bytes,int stride,int frames,int width) {
    return stride>=width&&stride<=512&&bytes>=width&&(int64_t)(frames-1)*stride+width<=bytes;
}
static float decoded(const uint8_t *p,VCPCMFormat f) {
    double x=sample(p,f);
    return (float)(isfinite(x)?fmax(-1,fmin(1,x)):0);
}
VCAudioRing *vc_audio_ring_create(int capacity_frames) {
    if(capacity_frames<2||capacity_frames>(1<<22))return NULL;
    uint32_t capacity=2;
    while(capacity<(uint32_t)capacity_frames)capacity<<=1;
    VCAudioRing *ring=calloc(1,sizeof *ring);
    if(!ring)return NULL;
    ring->samples=calloc((size_t)capacity*2,sizeof(float));
    if(!ring->samples) { free(ring);return NULL; }
    ring->capacity=capacity;ring->mask=capacity-1;
    atomic_init(&ring->write_index,0);atomic_init(&ring->read_index,0);
    atomic_init(&ring->packets,0);atomic_init(&ring->dropped,0);atomic_init(&ring->unsupported,0);
    atomic_init(&ring->channels,0);atomic_init(&ring->format,0);atomic_init(&ring->rate_bits,0);
    return ring;
}
void vc_audio_ring_destroy(VCAudioRing *ring) {
    if(!ring)return;
    free(ring->samples);free(ring);
}
int vc_audio_ring_capacity(VCAudioRing *ring) { return ring ? (int)ring->capacity : 0; }
int vc_audio_ring_write_pcm(VCAudioRing *ring,const void *left,int left_bytes,int left_stride,
    const void *right,int right_bytes,int right_stride,int frames,VCPCMFormat format,double rate) {
    int width=sample_size(format);
    if(!ring||!left||frames<=0||frames>192000||width==0||!isfinite(rate)||rate<8000||rate>192000||
       !view_fits(left_bytes,left_stride,frames,width)||(right&&!view_fits(right_bytes,right_stride,frames,width)))
        return 0;
    atomic_fetch_add_explicit(&ring->packets,1,memory_order_relaxed);
    uint64_t write=atomic_load_explicit(&ring->write_index,memory_order_relaxed);
    uint64_t read=atomic_load_explicit(&ring->read_index,memory_order_acquire);
    uint64_t space=ring->capacity-(write-read);
    int count=(uint64_t)frames>space ? (int)space : frames;
    if(count<frames)atomic_fetch_add_explicit(&ring->dropped,(uint64_t)(frames-count),memory_order_relaxed);
    const uint8_t *l=left,*r=right ? right : left;
    int rs=right ? right_stride : left_stride;
    for(int i=0;i<count;i++) {
        size_t slot=(size_t)((write+(uint64_t)i)&ring->mask)*2;
        ring->samples[slot]=decoded(l+(size_t)i*left_stride,format);
        ring->samples[slot+1]=decoded(r+(size_t)i*rs,format);
    }
    uint64_t bits;memcpy(&bits,&rate,sizeof bits);
    atomic_store_explicit(&ring->channels,right ? 2 : 1,memory_order_relaxed);
    atomic_store_explicit(&ring->format,(int)format,memory_order_relaxed);
    atomic_store_explicit(&ring->rate_bits,bits,memory_order_relaxed);
    atomic_store_explicit(&ring->write_index,write+(uint64_t)count,memory_order_release);
    return count;
}
void vc_audio_ring_note_unsupported(VCAudioRing *ring) {
    if(ring)atomic_fetch_add_explicit(&ring->unsupported,1,memory_order_relaxed);
}
int vc_audio_ring_available(VCAudioRing *ring) {
    if(!ring)return 0;
    uint64_t write=atomic_load_explicit(&ring->write_index,memory_order_acquire);
    uint64_t read=atomic_load_explicit(&ring->read_index,memory_order_relaxed);
    return (int)(write-read);
}
int vc_audio_ring_read(VCAudioRing *ring,float *left,float *right,int max_frames,VCAudioRingInfo *info) {
    if(!ring||!left||max_frames<=0)return 0;
    uint64_t write=atomic_load_explicit(&ring->write_index,memory_order_acquire);
    uint64_t read=atomic_load_explicit(&ring->read_index,memory_order_relaxed);
    uint64_t available=write-read;
    int count=available<(uint64_t)max_frames ? (int)available : max_frames;
    for(int i=0;i<count;i++) {
        size_t slot=(size_t)((read+(uint64_t)i)&ring->mask)*2;
        left[i]=ring->samples[slot];
        if(right)right[i]=ring->samples[slot+1];
    }
    if(info) {
        uint64_t bits=atomic_load_explicit(&ring->rate_bits,memory_order_relaxed);
        double rate;memcpy(&rate,&bits,sizeof rate);
        info->channels=atomic_load_explicit(&ring->channels,memory_order_relaxed);
        info->format=atomic_load_explicit(&ring->format,memory_order_relaxed);
        info->sample_rate=rate;
    }
    atomic_store_explicit(&ring->read_index,read+(uint64_t)count,memory_order_release);
    return count;
}
int vc_audio_ring_discard(VCAudioRing *ring,int frames) {
    if(!ring||frames<=0)return 0;
    uint64_t write=atomic_load_explicit(&ring->write_index,memory_order_acquire);
    uint64_t read=atomic_load_explicit(&ring->read_index,memory_order_relaxed);
    uint64_t available=write-read;
    int count=available<(uint64_t)frames ? (int)available : frames;
    atomic_store_explicit(&ring->read_index,read+(uint64_t)count,memory_order_release);
    return count;
}
VCAudioRingStats vc_audio_ring_stats(VCAudioRing *ring) {
    VCAudioRingStats stats={0};
    if(!ring)return stats;
    stats.packets=atomic_load_explicit(&ring->packets,memory_order_relaxed);
    stats.written=atomic_load_explicit(&ring->write_index,memory_order_acquire);
    stats.read=atomic_load_explicit(&ring->read_index,memory_order_acquire);
    stats.dropped=atomic_load_explicit(&ring->dropped,memory_order_relaxed);
    stats.unsupported=atomic_load_explicit(&ring->unsupported,memory_order_relaxed);
    return stats;
}
