// Spectrum Tunnel, Lunalyr's own: written for Windows from a description of the scene's behaviour (not a port).
//
// A first-person flight through a winding cave. The cave is a signed-distance bore around a centreline that snakes
// through space, so the bends hide the far end; the camera rides the centreline. The live spectrum is carved into the
// wall: each angular lane reads one slice of the 64 bands, bass on the floor and treble overhead, mirrored left/right,
// and loud lanes bulge the rock inward while the bass swells the whole bore. Spiral ridges corkscrew past (twist, depth
// from the low mids) and the waveform rings the bore along its length. Mids, presence and onsets thicken a haze; a
// kick sends a soft wall of light up the tunnel only when the scene's pulse option asks for it. It sits behind lyrics,
// so it stays dark and calm.
//
// The cave repeats every kLoop units along its length (every wave, ridge and noise lattice along z fits a whole number
// of times), so the flight wraps there without a seam and nothing loses float precision however long the clock runs.
#include "Common.hlsli"

// Option slots.
static const int kTwist = 0;   // how fast the spiral ridges corkscrew past
static const int kGlow = 1;    // light from the crevices
static const int kBend = 2;    // how much the centreline winds
static const int kPulse = 3;   // the kick's wall of light (0 on the desktop)
static const int kRipple = 4;  // the waveform's rings along the bore
static const int kFog = 5;     // the haze

static const int kSteps = 60;
static const float kFar = 26.0;
static const float kRadius = 1.55;
static const float kPi = 3.14159265;
static const float kLoop = 640.0;                  // the cave's length before it repeats
static const float kWave = 6.2831853 / 640.0;      // angular frequency of one swing per loop
static const float kTravel = 3.0;                  // cave units per unit of time * speed
static const float kGrain = 2.5;                   // rock grain (lattice cells per unit)
static const float kRockLoop = 80.0;               // the rock repeats every 80 units (8 times a loop): small hash inputs

// Where the centreline passes at depth z: two swings per axis on unrelated whole counts per loop, so the path never
// visibly repeats within a loop.
float2 centreline(float z)
{
    return opt(kBend) * float2(2.4 * sin(z * (21.0 * kWave)) + 1.1 * sin(z * (48.0 * kWave) + 1.9),
                               1.5 * sin(z * (17.0 * kWave) + 0.6) + 0.7 * cos(z * (40.0 * kWave)));
}

// Common's value noise (hash31), with the lattice wrapped every `period` cells along z so the rock repeats exactly
// with the loop and the hash only ever sees small coordinates.
float rockNoise(float3 x, float period)
{
    float3 i = floor(x), f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    float z0 = i.z - period * floor(i.z / period);
    float z1 = z0 + 1.0 >= period ? 0.0 : z0 + 1.0;
    float n000 = hash31(float3(i.x, i.y, z0));
    float n100 = hash31(float3(i.x + 1.0, i.y, z0));
    float n010 = hash31(float3(i.x, i.y + 1.0, z0));
    float n110 = hash31(float3(i.x + 1.0, i.y + 1.0, z0));
    float n001 = hash31(float3(i.x, i.y, z1));
    float n101 = hash31(float3(i.x + 1.0, i.y, z1));
    float n011 = hash31(float3(i.x, i.y + 1.0, z1));
    float n111 = hash31(float3(i.x + 1.0, i.y + 1.0, z1));
    return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
}

// Three octaves of it (as Common's fbm3, ~0...0.875), at kGrain cells per unit, repeating every kRockLoop units.
float rockFbm(float3 p)
{
    float3 x = p * kGrain;
    float v = 0.0, amp = 0.5, period = kRockLoop * kGrain;
    [unroll] for (int i = 0; i < 3; i++)
    {
        v += amp * rockNoise(x, period);
        x = x * 2.0 + float3(17.3, 5.1, 0.0);
        period *= 2.0;
        amp *= 0.5;
    }
    return v;
}

// The waveform at x in 0...1, interpolated between samples so the wall it shapes has no steps.
float waveSmooth(float x)
{
    float f = saturate(x) * 255.0;
    int i = min(int(f), 254);
    return lerp(waveValue(i), waveValue(i + 1), f - float(i));
}

// The waveform laid along the bore (ten times per loop), folded so it never seams, and low-passed (about 12 samples
// wide) so it rings rather than corrugates the wall.
float waveRings(float z)
{
    float x = abs(fract(z * (10.0 / kLoop)) * 2.0 - 1.0);
    const float tap = 3.0 / 255.0;
    return (waveSmooth(x - 2.0 * tap) + waveSmooth(x - tap) + waveSmooth(x) + waveSmooth(x + tap)
          + waveSmooth(x + 2.0 * tap)) * 0.2;
}

// Audio the wall reads, gathered once per pixel.
struct Drive
{
    float swell;    // the bass, widening the bore
    float ridge;    // the ridges' depth (the low mids deepen them)
    float spin;     // the ridges' phase
    float ring;     // the waveform's gain
};

// Cross-section angle around the bore: 0 on the floor, ±pi overhead. `q` is offset to stay finite on the axis.
float boreAngle(float2 q) { return atan2(q.x + 1e-5, -q.y); }

// The wall's radius at a point, and the lane's own energy.
float wallRadius(float3 p, Drive drive, out float lane, out float laneEnergy)
{
    float2 q = p.xy - centreline(p.z);
    float angle = boreAngle(q);
    lane = abs(angle) / kPi;                                   // 0 floor (bass) ... 1 ceiling (treble), mirrored
    laneEnergy = spectrumAlongRibbon(lane, sensitivity);
    float spiral = sin(4.0 * angle + p.z * (194.0 * kWave) + drive.spin);   // whole turns: no seam at ±pi
    float rings = drive.ring * waveRings(p.z);
    float rock = rockNoise(p * float3(1.3, 1.3, 0.9), 0.9 * kRockLoop);
    return kRadius + drive.swell
         - 0.42 * laneEnergy                                    // loud lanes bulge inward
         - drive.ridge * (0.5 + 0.5 * spiral)
         + rings
         + 0.26 * (rock - 0.5);
}

// Inside the bore the distance to the wall is positive.
float boreDistance(float3 p, Drive drive)
{
    float lane, laneEnergy;
    return wallRadius(p, drive, lane, laneEnergy) - length(p.xy - centreline(p.z));
}

float3 boreNormal(float3 p, Drive drive)
{
    const float2 e = float2(0.012, -0.012);
    // The distance grows towards the axis, so its gradient points off the wall into the bore.
    float3 g = e.xyy * boreDistance(p + e.xyy, drive) + e.yyx * boreDistance(p + e.yyx, drive)
             + e.yxy * boreDistance(p + e.yxy, drive) + e.xxx * boreDistance(p + e.xxx, drive);
    return normalize(g);
}

float4 main(FSQuadOut i) : SV_Target
{
    float2 uv = i.uv;
    float aspect = resolution.x / max(resolution.y, 1.0);
    float2 screen = (uv - 0.5) * float2(aspect, 1.0);
    float clock = time * speed;

    Drive drive;
    drive.swell = 0.30 * visualEnergy(bass, sensitivity);
    drive.ridge = 0.13 + 0.22 * visualEnergy(lowMid, sensitivity);
    drive.spin = -clock * (1.5 + 12.0 * opt(kTwist));
    drive.ring = 0.20 * opt(kRipple) * sqrt(max(sensitivity, 0.3));

    // The camera rides the centreline, looking a little ahead along it.
    float travel = clock * kTravel;
    travel -= kLoop * floor(travel / kLoop);
    float3 eye = float3(centreline(travel), travel);
    float3 ahead = float3(centreline(travel + 2.2), travel + 2.2);
    float3 forward = normalize(ahead - eye);
    float3 right = normalize(cross(float3(0.0, 1.0, 0.0), forward));
    float3 up = cross(forward, right);
    float3 ray = normalize(forward * 1.25 + right * screen.x + up * screen.y);

    // March, gathering haze on the way.
    float hazeDrive = visualEnergy(mid + 0.7 * presence + 2.0 * flux, sensitivity);
    float hazeDensity = opt(kFog) * (0.010 + 0.045 * hazeDrive);
    float t = 0.05, haze = 0.0;
    [loop] for (int s = 0; s < kSteps; s++)
    {
        float3 p = eye + ray * t;
        float d = boreDistance(p, drive);
        if (d < 0.003 * t) break;
        float step = max(d * 0.62, 0.02);
        haze += hazeDensity * step * exp(-0.11 * t) * (0.7 + 0.3 * sin(p.z * (92.0 * kWave) + clock * 1.8));
        t += step;
        if (t > kFar) break;
    }
    t = min(t, kFar);

    float3 p = eye + ray * t;
    float lane, laneEnergy;
    wallRadius(p, drive, lane, laneEnergy);
    float3 n = boreNormal(p, drive);

    // Rock: a dim stone tinted by the palette, lit by a soft lamp at the camera. Fine grain bumps the normal.
    const float nudge = 0.024;
    float detail = rockFbm(p);
    float3 slope = float3(rockFbm(p + float3(nudge, 0.0, 0.0)), rockFbm(p + float3(0.0, nudge, 0.0)),
                          rockFbm(p + float3(0.0, 0.0, nudge))) - detail;
    n = normalize(n - 0.6 * (slope / nudge) * exp(-0.12 * t));
    float3 hue = cosPalette(0.08 + 0.55 * lane + p.z * (20.0 / kLoop) + 0.01 * time, palette);
    float3 stone = lerp(float3(0.045, 0.040, 0.055), hue * 0.55, 0.30 + 0.55 * detail);
    float lamp = saturate(dot(n, -ray));
    float falloff = exp(-0.15 * t);
    float3 col = stone * (0.10 + 0.95 * lamp * lamp) * falloff * 1.7;

    // Crevices glow from within; the lanes the music fills glow with them.
    float recess = length(p.xy - centreline(p.z)) - (kRadius - 0.25);
    float crevice = smoothstep(0.0, 0.45, recess) * smoothstep(0.30, 0.75, detail + 0.25);
    float3 ember = cosPalette(0.18 + 0.6 * lane + 0.02 * time, palette);
    col += ember * opt(kGlow) * (0.10 * crevice + 0.30 * laneEnergy * (0.4 + 0.6 * lamp)) * exp(-0.09 * t);

    // A kick's soft wall of light, travelling up the tunnel towards the camera (only with the pulse option).
    float kick = max(soundKick, beat * 2.0);
    float wall = travel + 16.0 - 18.0 * min(kickAge, 2.0);
    col += ember * opt(kPulse) * 0.35 * kick * exp(-1.2 * abs(p.z - wall)) * falloff;

    // Unreached depth fades to the haze rather than to a hole.
    float far = smoothstep(kFar * 0.6, kFar, t);
    float3 mist = cosPalette(0.62 + 0.2 * spectralCentroid, palette);
    col = lerp(col, mist * 0.03, far);
    col += mist * haze * 0.55;

    // Exposure breathes with the level; Reinhard keeps it soft.
    col *= 0.85 + 0.35 * visualEnergy(level, sensitivity);
    col = col / (1.0 + col);
    return limitedExposure(albumTint(richness(col, opt(15)), uv));
}
