#ifndef VERSO_AUDIO_ANALYSIS_H
#define VERSO_AUDIO_ANALYSIS_H
typedef struct {
    double low, upper, rms, bass, mid, treble, average, pulse, since_beat;
} VCAudioState;
typedef struct { double rms, bass, mid, treble, pulse; } VCAudioLevels;
typedef enum { VC_PCM_FLOAT32=1, VC_PCM_FLOAT64, VC_PCM_INT16, VC_PCM_INT24, VC_PCM_INT32 } VCPCMFormat;
typedef struct { double swell, pulse, bass, mid, treble; } VCAudioVisualState;
/* Bounds-checked native-endian PCM, including planar/interleaved channel views. */
VCAudioLevels vc_audio_process_pcm(VCAudioState *state, const void *bytes, int byte_count,
    int frames, int stride_bytes, VCPCMFormat format, double sample_rate);
/* Independent visual smoothing: analysis sensitivity never multiplies twice. */
VCAudioVisualState vc_audio_visual(VCAudioVisualState *state, VCAudioLevels raw, double sensitivity, double dt);
/* Float PCM, including a strided channel of an interleaved stream. No allocation. */
VCAudioLevels vc_audio_process(VCAudioState *state, const float *samples,
                              int frames, int stride, double sample_rate);
#endif
