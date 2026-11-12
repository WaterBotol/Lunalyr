// Light Trails: five thin streaks of light along gently curving lanes across a dim fog. Each lane has a bright head
// travelling along it and a tail fading behind; a head fades in as it enters and out as it leaves, then starts again.
// A sparse dust of faint, slowly twinkling points.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float extent = 0.5 * Size.x / min(Size.x, Size.y);
    float lum = 0.045 + 0.07 * Noise(q * 1.6 + float2(t * 0.03, 0.0));

    [unroll] for (int i = 0; i < 5; i++)
    {
        float fi = (float)i;
        float heading = (i == 1 || i == 3) ? -1.0 : 1.0;
        float lane = -0.34 + 0.17 * fi + 0.06 * sin(fi * 2.9);
        float y = lane + 0.08 * sin(q.x * (1.1 + 0.2 * fi) + fi * 2.3 + t * 0.02) + 0.03 * sin(q.x * 2.7 + fi);
        float d = abs(q.y - y);
        float u = frac(t * (0.085 + 0.035 * frac(fi * 0.618034)) + fi * 0.37);
        float head = lerp(-extent - 0.1, extent + 0.6, u);
        float behind = head - q.x * heading;
        float tail = behind > 0.0 ? exp(-behind / 0.42) : exp(behind / 0.01);
        float envelope = smoothstep(0.0, 0.08, u) * (1.0 - smoothstep(0.88, 1.0, u));
        float core = exp(-d * d / 1.2e-5);
        float halo = exp(-d / 0.018);
        float spark = exp(-(behind * behind + d * d) / 4e-4);
        lum += envelope * (tail * (0.55 * core + 0.16 * halo) + 0.75 * spark);
    }

    // Dust: at most one faint point in each 18-DIP cell.
    float2 cell = floor(p / 18.0);
    float2 h = Random2(cell);
    float2 spot = (cell + 0.2 + 0.6 * h) * 18.0;
    float2 dd = p - spot;
    float present = step(h.x * 0.7 + h.y * 0.3, 0.22);
    lum += present * 0.22 * exp(-dot(dd, dd) / 1.4) * (0.6 + 0.4 * sin(Time * 0.7 + h.y * Tau));
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
