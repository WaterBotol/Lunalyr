#include "../Sources/Support/AudioAnalysis.h"
#include <assert.h>
#include <math.h>
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
    puts("Audio bands, onsets, silence, striding and finite-input checks passed.");
}
