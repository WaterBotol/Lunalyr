// The photosensitivity limiter's measuring pass, from Lunalyr for Mac's ExposureLimiter.metal (Lunalyr's own model,
// written here as a pixel shader so feature level 10 devices run it too). With Reduce Motion on, each frame is drawn
// first at 64 × 36 into a probe; this pass, drawn into a 1 × 1 target, measures the probe's mean relative luminance
// and limits how fast the displayed mean may rise (falls stay immediate); then the full frame multiplies its output by
// the gain it returns. The GPU orders the three passes, so the gain belongs to the frame it limits and the CPU never
// waits. Lunalyr.Core's ExposureLimiter is the same model on the CPU, with its tests.
//   in:  t0 the probe (sRGB-encoded), t1 the previous state (x gain, y displayed mean), b0 the parameters
//   out: float2(gain for sRGB-encoded output, displayed mean linear luminance)

Texture2D<float4> Probe : register(t0);
Texture2D<float2> Previous : register(t1);

cbuffer Params : register(b0)
{
    float maxRise; // linear luminance per second
    float stepSeconds; // seconds since the previous measured frame
    float adopt; // 1 when the limiter just turned on: take this frame's mean
    float unusedParam;
};

float2 main(float4 position : SV_Position) : SV_Target
{
    uint width, height;
    Probe.GetDimensions(width, height);
    float sum = 0.0;
    [loop] for (uint y = 0; y < height; y++)
    {
        [loop] for (uint x = 0; x < width; x++)
        {
            float3 encoded = saturate(Probe.Load(int3(x, y, 0)).rgb);
            float3 lin = encoded <= 0.04045 ? encoded / 12.92 : pow((encoded + 0.055) / 1.055, 2.4);
            sum += dot(lin, float3(0.2126, 0.7152, 0.0722));
        }
    }
    float mean = sum / float(max(width * height, 1u));
    float step = maxRise * clamp(stepSeconds, 0.0, 0.1);
    float shown = adopt > 0.5 ? mean : min(mean, Previous.Load(int3(0, 0, 0)).y + step);
    float gain = mean > 0.0001 ? pow(saturate(shown / mean), 1.0 / 2.2) : 1.0;
    return float2(gain, shown);
}
