// Photosensitivity limiter for the native scenes. With Reduce Motion or Dim
// Flashing Lights on, the host renders each frame twice into one command
// buffer: first at 64 × 36 into a probe texture, then this kernel measures
// the probe's mean relative luminance and updates a per-surface gain, then
// the full frame multiplies its output by that gain (buffer 5). The GPU
// orders the three passes, so the gain belongs to the frame it limits and
// the CPU never waits. ExposureLimiter in VisualizerSafety.swift is the same
// model on the CPU and documents the threshold.
#include <metal_stdlib>
using namespace metal;

// A new surface starts at shown 0 and fades in from black; one taking over a
// scene that is on screen starts at the level it is shown at (ExposureMemory).
struct ExposureState {
    float gain;    // multiplies sRGB-encoded output
    float shown;   // displayed mean linear luminance
    float unused0;
    float unused1;
};

struct ExposureParams {
    float maxRise; // linear luminance per second
    float dt;      // seconds since the previous measured frame
    float adopt;   // 1 when the limiter just turned on: take this frame's mean
    float unused;
};

kernel void exposureLimiter(texture2d<float, access::read> probe [[texture(0)]],
                            device ExposureState& state [[buffer(0)]],
                            constant ExposureParams& params [[buffer(1)]],
                            uint index [[thread_index_in_threadgroup]],
                            uint count [[threads_per_threadgroup]]) {
    threadgroup float partial[256];
    uint width = probe.get_width(), height = probe.get_height();
    uint total = width * height;
    float sum = 0.0;
    for (uint texel = index; texel < total; texel += count) {
        float3 encoded = clamp(probe.read(uint2(texel % width, texel / width)).rgb, 0.0, 1.0);
        float3 linear = select(pow((encoded + 0.055) / 1.055, 2.4), encoded / 12.92, encoded <= 0.04045);
        sum += dot(linear, float3(0.2126, 0.7152, 0.0722));
    }
    partial[index] = sum;
    threadgroup_barrier(mem_flags::mem_threadgroup);
    for (uint stride = count / 2; stride > 0; stride /= 2) {
        if (index < stride) { partial[index] += partial[index + stride]; }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }
    if (index != 0) { return; }
    float mean = partial[0] / float(max(total, 1u));
    float step = params.maxRise * clamp(params.dt, 0.0, 0.1);
    float shown = params.adopt > 0.5 ? mean : min(mean, state.shown + step);
    state.shown = shown;
    state.gain = mean > 0.0001 ? pow(clamp(shown / mean, 0.0, 1.0), 1.0 / 2.2) : 1.0;
}
