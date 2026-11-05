#include "../Sources/Support/AudioAnalysis.h"
#include <assert.h>
#include <math.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdio.h>
#include <string.h>
#include <stdint.h>

static VCAudioLevels tone(double hz) {
    VCAudioState state={0};VCAudioLevels value={0};float pcm[960];
    for(int block=0;block<80;block++) {
        for(int i=0;i<960;i++)pcm[i]=(float)(.25*sin(6.283185307179586*hz*(block*960+i)/48000));
        value=vc_audio_process(&state,pcm,960,1,48000);
    }
    assert(value.rms>.17&&value.rms<.18);
    return value;
}

/* Ring checks: frames carry their own sequence number so wraparound,
   overrun and concurrent hand-off can be verified sample by sample. */
static void ramp(float *pcm,int frames,int start) {
    for(int i=0;i<frames;i++) { pcm[i*2]=(float)((start+i)%1000)/1000.0f;pcm[i*2+1]=-pcm[i*2]; }
}
static int write_ramp(VCAudioRing *ring,int frames,int start) {
    float pcm[2*512];assert(frames<=512);ramp(pcm,frames,start);
    return vc_audio_ring_write_pcm(ring,pcm,(int)sizeof(float)*2*frames,8,pcm+1,(int)sizeof(float)*2*frames-4,8,
        frames,VC_PCM_FLOAT32,48000);
}
static void ring_checks(void) {
    assert(vc_audio_ring_create(1)==NULL&&vc_audio_ring_create((1<<22)+1)==NULL);
    VCAudioRing *ring=vc_audio_ring_create(1000);
    assert(ring&&vc_audio_ring_capacity(ring)==1024);
    float left[1024],right[1024];VCAudioRingInfo info={0};
    /* Wraparound: repeatedly cross the end of the storage. */
    int next=0,expected=0;
    for(int round=0;round<40;round++) {
        assert(write_ramp(ring,300,next)==300);next+=300;
        int count=vc_audio_ring_read(ring,left,right,1024,&info);
        assert(count==300&&info.channels==2&&info.format==VC_PCM_FLOAT32&&info.sample_rate==48000);
        for(int i=0;i<count;i++,expected++) {
            float value=(float)(expected%1000)/1000.0f;
            assert(left[i]==value&&right[i]==-value);
        }
    }
    assert(vc_audio_ring_available(ring)==0&&vc_audio_ring_read(ring,left,right,1024,&info)==0);
    /* Overrun: a stalled consumer keeps the oldest frames and counts the rest. */
    for(int i=0;i<3;i++)assert(write_ramp(ring,300,next+i*300)==300);
    assert(write_ramp(ring,300,next+900)==124);
    assert(write_ramp(ring,300,next+1200)==0);
    VCAudioRingStats stats=vc_audio_ring_stats(ring);
    assert(stats.dropped==176+300&&stats.written==stats.read+1024&&stats.packets==40+5);
    assert(vc_audio_ring_available(ring)==1024);
    assert(vc_audio_ring_read(ring,left,NULL,1024,NULL)==1024);
    for(int i=0;i<1024;i++)assert(left[i]==(float)((next+i)%1000)/1000.0f);
    /* The consumer may skip a stale backlog. */
    next+=1500;
    assert(write_ramp(ring,500,next)==500&&vc_audio_ring_discard(ring,450)==450);
    assert(vc_audio_ring_read(ring,left,right,1024,&info)==50&&left[0]==(float)((next+450)%1000)/1000.0f);
    assert(vc_audio_ring_discard(ring,10)==0);
    /* Mono views fill both slots; integer PCM is decoded on the way in. */
    int16_t mono[256];for(int i=0;i<256;i++)mono[i]=(int16_t)(i*64-8192);
    assert(vc_audio_ring_write_pcm(ring,mono,(int)sizeof(mono),2,NULL,0,0,256,VC_PCM_INT16,44100)==256);
    assert(vc_audio_ring_read(ring,left,right,1024,&info)==256&&info.channels==1&&info.sample_rate==44100);
    for(int i=0;i<256;i++)assert(left[i]==(float)(mono[i]/32768.0)&&right[i]==left[i]);
    /* A trailing channel of a 4-channel interleaved Float32 buffer. */
    float wide[4*64];for(int i=0;i<64;i++) { wide[i*4]=.9f;wide[i*4+1]=.9f;wide[i*4+2]=(float)i/64;wide[i*4+3]=-(float)i/64; }
    assert(vc_audio_ring_write_pcm(ring,wide+2,(int)sizeof(wide)-8,16,wide+3,(int)sizeof(wide)-12,16,64,VC_PCM_FLOAT32,48000)==64);
    assert(vc_audio_ring_read(ring,left,right,1024,&info)==64);
    for(int i=0;i<64;i++)assert(left[i]==(float)i/64&&right[i]==-(float)i/64);
    /* Malformed views and rates are rejected without counting a packet. */
    uint64_t packets=vc_audio_ring_stats(ring).packets;
    assert(vc_audio_ring_write_pcm(ring,wide,10,4,NULL,0,0,64,VC_PCM_FLOAT32,48000)==0);
    assert(vc_audio_ring_write_pcm(ring,wide,(int)sizeof(wide),2,NULL,0,0,64,VC_PCM_FLOAT32,48000)==0);
    assert(vc_audio_ring_write_pcm(ring,wide,(int)sizeof(wide),16,wide+1,8,16,64,VC_PCM_FLOAT32,48000)==0);
    assert(vc_audio_ring_write_pcm(ring,wide,(int)sizeof(wide),16,NULL,0,0,64,VC_PCM_FLOAT32,NAN)==0);
    assert(vc_audio_ring_write_pcm(ring,wide,(int)sizeof(wide),16,NULL,0,0,64,(VCPCMFormat)99,48000)==0);
    assert(vc_audio_ring_write_pcm(NULL,wide,(int)sizeof(wide),16,NULL,0,0,64,VC_PCM_FLOAT32,48000)==0);
    assert(vc_audio_ring_stats(ring).packets==packets);
    vc_audio_ring_note_unsupported(ring);
    assert(vc_audio_ring_stats(ring).unsupported==1);
    /* Non-finite input never reaches the analysers. */
    float bad[4]={NAN,INFINITY,-INFINITY,2};
    assert(vc_audio_ring_write_pcm(ring,bad,(int)sizeof(bad),4,NULL,0,0,4,VC_PCM_FLOAT32,48000)==4);
    assert(vc_audio_ring_read(ring,left,right,1024,NULL)==4);
    assert(left[0]==0&&left[1]==0&&left[2]==0&&left[3]==1);
    vc_audio_ring_destroy(ring);
    vc_audio_ring_destroy(NULL);
}
typedef struct { VCAudioRing *ring; int blocks; _Atomic int done; } Producer;
static void *produce(void *argument) {
    Producer *job=argument;
    for(int block=0;block<job->blocks;block++) {
        float pcm[2*256];
        /* Samples are clamped to -1...1, so split the frame number across
           the channels: low eleven bits left, the rest right. */
        for(int i=0;i<256;i++) {
            int frame=block*256+i;
            pcm[i*2]=(float)(frame%2048)/2048;pcm[i*2+1]=(float)(frame/2048)/2048;
        }
        int stored=vc_audio_ring_write_pcm(job->ring,pcm,(int)sizeof(pcm),8,pcm+1,(int)sizeof(pcm)-4,8,256,VC_PCM_FLOAT32,48000);
        assert(stored>=0&&stored<=256);
    }
    atomic_store_explicit(&job->done,1,memory_order_release);
    return NULL;
}
static void ring_concurrency_check(void) {
    /* One producer, one consumer: every frame read is intact and in order;
       anything missing was reported as dropped. */
    VCAudioRing *ring=vc_audio_ring_create(4096);
    Producer job={ring,4000,0};pthread_t thread;
    assert(pthread_create(&thread,NULL,produce,&job)==0);
    float left[1024],right[1024];int last=-1;uint64_t seen=0;
    for(;;) {
        /* Check completion before reading, so the final read sees everything. */
        int finished=atomic_load_explicit(&job.done,memory_order_acquire);
        int count=vc_audio_ring_read(ring,left,right,1024,NULL);
        for(int i=0;i<count;i++) {
            float low=left[i]*2048,high=right[i]*2048;
            assert(low==floorf(low)&&high==floorf(high));
            int frame=(int)low+(int)high*2048;
            /* In order; a gap is allowed only at a block boundary (a drop). */
            assert(frame>last&&(frame==last+1||frame%256==0));
            last=frame;seen++;
        }
        if(count==0&&finished)break;
    }
    assert(pthread_join(thread,NULL)==0);
    VCAudioRingStats stats=vc_audio_ring_stats(ring);
    assert(seen==stats.read&&stats.read+stats.dropped==(uint64_t)job.blocks*256);
    vc_audio_ring_destroy(ring);
}
int main(void) {
    /* Equivalent PCM representations must produce the same real energy. */
    float f32[960];double f64[960];int16_t i16[960];int32_t i32[960];unsigned char i24[2880];
    for(int i=0;i<960;i++) {
        double x=.25*sin(6.283185307179586*240*i/48000);
        f32[i]=(float)x;f64[i]=x;i16[i]=(int16_t)(x*32768);i32[i]=(int32_t)(x*2147483648.0);
        uint32_t bits=(uint32_t)(int32_t)(x*8388608);
        i24[i*3]=(unsigned char)bits;i24[i*3+1]=(unsigned char)(bits>>8);i24[i*3+2]=(unsigned char)(bits>>16);
    }
    VCAudioState s32={0},s64={0},s16={0},s24={0},si32={0};
    VCAudioLevels reference=vc_audio_process_pcm(&s32,f32,sizeof(f32),960,4,VC_PCM_FLOAT32,48000);
    VCAudioLevels sixtyfour=vc_audio_process_pcm(&s64,f64,sizeof(f64),960,8,VC_PCM_FLOAT64,48000);
    VCAudioLevels sixteen=vc_audio_process_pcm(&s16,i16,sizeof(i16),960,2,VC_PCM_INT16,48000);
    VCAudioLevels twentyfour=vc_audio_process_pcm(&s24,i24,sizeof(i24),960,3,VC_PCM_INT24,48000);
    VCAudioLevels thirtytwo=vc_audio_process_pcm(&si32,i32,sizeof(i32),960,4,VC_PCM_INT32,48000);
    assert(fabs(reference.rms-sixtyfour.rms)<1e-7);
    assert(fabs(reference.rms-sixteen.rms)<2e-5);
    assert(fabs(reference.rms-twentyfour.rms)<2e-7);
    assert(fabs(reference.rms-thirtytwo.rms)<1e-7);
    assert(vc_audio_process_pcm(&s32,f32,10,960,4,VC_PCM_FLOAT32,48000).rms==0);
    assert(vc_audio_process_pcm(&s32,f32,sizeof(f32),960,1,VC_PCM_FLOAT32,48000).rms==0);
    assert(vc_audio_process_pcm(&s32,f32,sizeof(f32),960,4,(VCPCMFormat)99,48000).rms==0);
    /* The FFT decoder reads the same bounded, strided PCM view. */
    float decoded[960];
    assert(vc_audio_decode_pcm(f64,sizeof(f64),960,8,VC_PCM_FLOAT64,decoded,960)==960);
    for(int i=0;i<960;i++)assert(fabs(decoded[i]-f32[i])<1e-7);
    assert(vc_audio_decode_pcm(i24,sizeof(i24),960,3,VC_PCM_INT24,decoded,960)==960);
    for(int i=0;i<960;i++)assert(fabs(decoded[i]-f32[i])<2e-7);
    assert(vc_audio_decode_pcm(f32,10,960,4,VC_PCM_FLOAT32,decoded,960)==0);
    assert(vc_audio_decode_pcm(f32,sizeof(f32),960,4,VC_PCM_FLOAT32,decoded,10)==0);
    VCAudioLevels low=tone(80),middle=tone(1000),high=tone(8000);
    assert(low.bass>low.mid*1.8&&low.bass>low.treble*8);
    assert(middle.mid>middle.bass*3&&middle.mid>middle.treble*1.5);
    assert(high.treble>high.mid*2&&high.treble>high.bass*10);
    VCAudioState state={0};float pcm[1920];memset(pcm,0,sizeof(pcm));
    VCAudioLevels value=vc_audio_process(&state,pcm,960,1,48000);
    assert(value.rms==0&&value.pulse==0);
    for(int i=0;i<960;i++)pcm[i]=(float)(.5*sin(6.283185307179586*80*i/48000));
    value=vc_audio_process(&state,pcm,960,1,48000);
    assert(value.pulse>.4); /* actual amplitude transient, no lyric clock */
    memset(pcm,0,sizeof(pcm));
    for(int i=0;i<180;i++)value=vc_audio_process(&state,pcm,960,1,48000);
    assert(value.rms<1e-6&&value.pulse<1e-6);
    VCAudioState interleaved={0},mono={0};float mono_pcm[960];
    for(int i=0;i<960;i++) { mono_pcm[i]=(float)(.25*sin(i*.1));pcm[i*2]=mono_pcm[i];pcm[i*2+1]=0; }
    VCAudioLevels a=vc_audio_process(&interleaved,pcm,960,2,48000);
    VCAudioLevels b=vc_audio_process(&mono,mono_pcm,960,1,48000);
    assert(fabs(a.rms-b.rms)<1e-12&&fabs(a.bass-b.bass)<1e-12);
    pcm[0]=NAN;pcm[1]=INFINITY;
    value=vc_audio_process(&state,pcm,960,1,48000);
    assert(isfinite(value.rms)&&isfinite(value.bass)&&isfinite(value.pulse));
    assert(vc_audio_process(NULL,pcm,960,1,48000).rms==0);
    assert(vc_audio_process(&state,NULL,960,1,48000).rms==0);
    assert(vc_audio_process(&state,pcm,960,0,48000).rms==0);
    assert(vc_audio_process(&state,pcm,960,1,NAN).rms==0);
    ring_checks();
    ring_concurrency_check();
    puts("Audio bands, onsets, silence, striding, finite-input and ring checks passed.");
}
