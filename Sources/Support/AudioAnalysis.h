#ifndef VERSO_AUDIO_ANALYSIS_H
#define VERSO_AUDIO_ANALYSIS_H
#include <stdint.h>
typedef struct {
    double low, upper, rms, bass, mid, treble, average, pulse, since_beat;
} VCAudioState;
typedef struct { double rms, bass, mid, treble, pulse; } VCAudioLevels;
typedef enum { VC_PCM_FLOAT32=1, VC_PCM_FLOAT64, VC_PCM_INT16, VC_PCM_INT24, VC_PCM_INT32 } VCPCMFormat;
/* Decode a bounded channel view for the FFT without assuming Float32. */
int vc_audio_decode_pcm(const void *bytes, int byte_count, int frames, int stride_bytes,
    VCPCMFormat format, float *output, int capacity);
/* Bounds-checked native-endian PCM, including planar/interleaved channel views. */
VCAudioLevels vc_audio_process_pcm(VCAudioState *state, const void *bytes, int byte_count,
    int frames, int stride_bytes, VCPCMFormat format, double sample_rate);
/* Float PCM, including a strided channel of an interleaved stream. No allocation. */
VCAudioLevels vc_audio_process(VCAudioState *state, const float *samples,
                              int frames, int stride, double sample_rate);

/* A single-producer, single-consumer ring of decoded stereo frames. The
   producer is a real-time audio callback: writing never locks, allocates or
   waits, and frames which do not fit are dropped and counted. The consumer
   drains on a worker thread. Create and destroy outside the audio thread. */
typedef struct VCAudioRing VCAudioRing;
/* What the producer last wrote: 1 or 2 channels, its PCM encoding and rate. */
typedef struct { int channels; int format; double sample_rate; } VCAudioRingInfo;
/* Totals since creation. `packets` counts accepted producer callbacks. */
typedef struct { uint64_t packets, written, read, dropped, unsupported; } VCAudioRingStats;
/* capacity_frames is rounded up to a power of two; returns NULL if invalid. */
VCAudioRing *vc_audio_ring_create(int capacity_frames);
void vc_audio_ring_destroy(VCAudioRing *ring);
int vc_audio_ring_capacity(VCAudioRing *ring);
/* Producer only. Decodes bounds-checked PCM channel views (right may be NULL
   for mono) straight into the ring. Returns the number of frames stored. */
int vc_audio_ring_write_pcm(VCAudioRing *ring, const void *left, int left_bytes, int left_stride,
    const void *right, int right_bytes, int right_stride, int frames, VCPCMFormat format, double sample_rate);
/* Producer only. Records a callback whose format could not be decoded. */
void vc_audio_ring_note_unsupported(VCAudioRing *ring);
/* Consumer only. Copies up to max_frames into planar buffers (right may be
   NULL), oldest first, and reports the producer's latest stream details. */
int vc_audio_ring_read(VCAudioRing *ring, float *left, float *right, int max_frames, VCAudioRingInfo *info);
/* Consumer only. Frames waiting to be read, and skipping the oldest of them. */
int vc_audio_ring_available(VCAudioRing *ring);
int vc_audio_ring_discard(VCAudioRing *ring, int frames);
/* Either side. */
VCAudioRingStats vc_audio_ring_stats(VCAudioRing *ring);
#endif
