// The thinking-orbs engine, ported to C# (the Mac's ThinkingOrbEngine.swift, 64eee43): a straight transcription of
// thinking-orbs' src/engine (core, lattice, orbits, braid, web, ribbon, morph, profiles) and src/presets.ts at commit
// de85557ca220332586d070d8788c0e1d6e877a0d (npm thinking-orbs 0.3.1, spec 1.0.0),
// https://github.com/Jakubantalik/thinking-orbs. Pure geometry: (size, t, opts) → a z-sorted list of dots and lines.
// One local hook: wave reads an optional "swell" option (default 1, upstream's).
//
// Transcription notes: JS Math.round is round-half-up (Floor(x + 0.5) here, not C#'s banker's rounding); the final
// z-sort is stable (ties keep their emit order); a missing option takes the default written in each formula.
//
// MIT License
//
// Copyright (c) 2026 Jakub Antalik
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

namespace Lunalyr.Core.Motion.ThinkingOrbs;

/// <summary>One dot: position (in a <c>size</c>-wide frame), depth, radius, ink (0 = darkest on paper) and alpha.</summary>
public record struct OrbDot(double X, double Y, double Z, double R, double White, double A = 1)
{
    /// <summary>Emit order, so the z-sort is stable.</summary>
    internal int Order;
}

/// <summary>A stroked edge (the connecting web), drawn before the dots.</summary>
public readonly record struct OrbLine(double X1, double Y1, double X2, double Y2, double White, double A, double W);

/// <summary>The nine upstream states.</summary>
public enum OrbState { Working, Searching, Solving, Listening, Connecting, Weaving, Composing, Breathing, Shaping }

/// <summary>The engine's geometry modes (ring shares ribbon's geometry).</summary>
public enum OrbMode { Orbits, Globe, Rubik, Wave, Web, Braid, Ribbon, Ring, Morph }

/// <summary>A mode's options: a bag of numbers; a missing key takes the default written in the formula.</summary>
public sealed class OrbOpts
{
    private readonly Dictionary<string, double> _values;

    public OrbOpts(IEnumerable<KeyValuePair<string, double>>? values = null) =>
        _values = values is null ? new(StringComparer.Ordinal) : new(values, StringComparer.Ordinal);

    public double this[string key, double fallback] => _values.TryGetValue(key, out var v) ? v : fallback;

    public double? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    /// <summary>JS truthiness of a numeric option: present and not 0.</summary>
    public bool Flag(string key) => _values.TryGetValue(key, out var v) && v != 0 && !double.IsNaN(v);

    public IReadOnlyDictionary<string, double> Values => _values;

    public OrbOpts With(string key, double value)
    {
        var copy = new OrbOpts(_values) { _values = { [key] = value } };
        return copy;
    }

    internal Dictionary<string, double> Copy() => new(_values, StringComparer.Ordinal);
}

/// <summary>A (state, size) resolved: its mode, clock speed and fully scaled options.</summary>
public sealed record OrbResolved(OrbMode Mode, double Speed, OrbOpts Opts);

/// <summary>A finished frame, reusing its buffers: lines (draw first), then dots far to near.</summary>
public sealed class OrbFrame
{
    public List<OrbDot> Dots { get; } = new(1024);
    public List<OrbLine> Lines { get; } = new(256);

    public void Clear()
    {
        Dots.Clear();
        Lines.Clear();
    }
}

public static class ThinkingOrbEngine
{
    // ---- core.ts ------------------------------------------------------------------------------------------------

    /// <summary>JS Math.round: round half up.</summary>
    public static double Round(double x) => Math.Floor(x + 0.5);

    public static double Lerp(double a, double b, double f) => a + (b - a) * f;

    public static double Frac(double x) => x - Math.Floor(x);

    /// <summary>Deterministic hash in [0, 1).</summary>
    public static double HashD(double a, double b)
    {
        var h = Math.Sin(a * 12.9898 + b * 78.233) * 43758.5453;
        return h - Math.Floor(h);
    }

    /// <summary>Value noise on a 2D lattice.</summary>
    public static double VNoise(double x, double y)
    {
        var xi = Math.Floor(x);
        var yi = Math.Floor(y);
        var fx = x - xi;
        var fy = y - yi;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        var a = HashD(xi, yi);
        var b = HashD(xi + 1, yi);
        var c = HashD(xi, yi + 1);
        var d = HashD(xi + 1, yi + 1);
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }

    /// <summary>Stable directions on a unit sphere (Fibonacci lattice).</summary>
    public static (double X, double Y, double Z) FibDir(int i, int n)
    {
        var golden = Math.PI * (3 - Math.Sqrt(5));
        var y = 1 - 2 * (i + 0.5) / n;
        var rad = Math.Sqrt(1 - y * y);
        var a = i * golden;
        return (rad * Math.Cos(a), y, rad * Math.Sin(a));
    }

    /// <summary>Shortest signed angular distance, wrapped to (-π, π].</summary>
    public static double AngleDelta(double a, double b) => Math.Atan2(Math.Sin(a - b), Math.Cos(a - b));

    /// <summary>Spin + tilt + orthographic projection.</summary>
    private readonly struct Proj(double yaw, double tilt, double cx, double cy, double scale)
    {
        private readonly double _st = Math.Sin(tilt), _ct = Math.Cos(tilt), _sy = Math.Sin(yaw), _cy = Math.Cos(yaw);

        public (double X, double Y, double Z) Of(double x, double y, double z)
        {
            var x1 = x * _cy + z * _sy;
            var z1 = -x * _sy + z * _cy;
            var y1 = y * _ct - z1 * _st;
            var z2 = y * _st + z1 * _ct;
            return (cx + x1 * scale, cy - y1 * scale, z2);
        }
    }

    /// <summary>Dot radii were tuned for a 300 pt frame; sub-linear scaling keeps small orbs legible.</summary>
    public static double RadiusScale(double size, double pow) => Math.Pow(size / 300, pow);

    private sealed class ByDepth : IComparer<OrbDot>
    {
        public static readonly ByDepth Instance = new();
        public int Compare(OrbDot a, OrbDot b) => a.Z < b.Z ? -1 : a.Z > b.Z ? 1 : a.Order.CompareTo(b.Order);
    }

    /// <summary>Drop invisible marks (a &lt; 0.02), clamp radii to <paramref name="rMin"/>, z-sort far to near (stable).</summary>
    private static void Finalize(OrbFrame frame, double rMin)
    {
        var dots = frame.Dots;
        var kept = 0;
        for (var i = 0; i < dots.Count; i++)
        {
            var d = dots[i];
            if (d.A < 0.02) continue;
            d.R = Math.Max(rMin, d.R);
            d.Order = i;
            dots[kept++] = d;
        }
        dots.RemoveRange(kept, dots.Count - kept);
        dots.Sort(ByDepth.Instance);
        var lines = frame.Lines;
        var keptLines = 0;
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].A >= 0.02) lines[keptLines++] = lines[i];
        lines.RemoveRange(keptLines, lines.Count - keptLines);
    }

    // ---- registry.ts ----------------------------------------------------------------------------------------------

    /// <summary>The frame for <paramref name="mode"/> at <paramref name="size"/> and engine time <paramref name="t"/>, into <paramref name="frame"/>.</summary>
    public static OrbFrame Frame(OrbMode mode, double size, double t, OrbOpts o, OrbFrame? frame = null)
    {
        frame ??= new OrbFrame();
        frame.Clear();
        switch (mode)
        {
            case OrbMode.Orbits: Orbits(size, t, o, frame); break;
            case OrbMode.Globe: Globe(size, t, o, frame); break;
            case OrbMode.Rubik: Rubik(size, t, o, frame); break;
            case OrbMode.Wave: Wave(size, t, o, frame); break;
            case OrbMode.Web: Web(size, t, o, frame); break;
            case OrbMode.Braid: Braid(size, t, o, frame); break;
            case OrbMode.Ribbon:
            case OrbMode.Ring: Ribbon(size, t, o, frame); break;
            default: Morph(size, t, o, frame); break;
        }
        Finalize(frame, o["rMin", 0.3]);
        return frame;
    }

    // ---- lattice.ts -------------------------------------------------------------------------------------------------

    private readonly record struct Move(int Axis, double Lo, double Hi, double Ang);

    private static (double[] Amount, int Active) SolveCycle(double time, int count, double slotDur, double rest, double[] amount)
    {
        var cyc = 2 * count * slotDur + rest;
        var tc = time % cyc;
        Array.Clear(amount);
        var active = -1;
        if (tc < 2 * count * slotDur)
        {
            var slot = (int)Math.Floor(tc / slotDur);
            var p = (tc - slot * slotDur) / slotDur;
            var cl = Math.Min(1, p / 0.7);
            var ep = 1 - Math.Pow(1 - cl, 3);
            if (slot < count)
            {
                for (var i = 0; i < slot; i++) amount[i] = 1;
                amount[slot] = ep;
                active = slot;
            }
            else
            {
                var u = 2 * count - 1 - slot;
                for (var i = 0; i < u; i++) amount[i] = 1;
                amount[u] = 1 - ep;
                active = u;
            }
        }
        return (amount, active);
    }

    private static (double X, double Y, double Z, bool InActive) ApplyMoves(double x, double y, double z, Move[] moves,
                                                                          double[] amount, int active)
    {
        var inActive = false;
        for (var i = 0; i < moves.Length; i++)
        {
            if (amount[i] <= 0) continue;
            var mv = moves[i];
            var coord = mv.Axis == 0 ? x : mv.Axis == 1 ? y : z;
            if (coord < mv.Lo || coord >= mv.Hi) continue;
            if (i == active) inActive = true;
            var a = mv.Ang * amount[i];
            var ca = Math.Cos(a);
            var sa = Math.Sin(a);
            if (mv.Axis == 0)
            {
                var y2 = y * ca - z * sa;
                z = y * sa + z * ca;
                y = y2;
            }
            else if (mv.Axis == 1)
            {
                var x2 = x * ca + z * sa;
                z = -x * sa + z * ca;
                x = x2;
            }
            else
            {
                var x2 = x * ca - y * sa;
                y = x * sa + y * ca;
                x = x2;
            }
        }
        return (x, y, z, inActive);
    }

    private static Move[] MakeMoves(int count)
    {
        var moves = new Move[count];
        for (var i = 0; i < count; i++)
        {
            var axis = (int)Math.Min(2, Math.Floor(HashD(i, 2.3) * 3));
            var lo = -1.0 + 0.5 * Math.Min(3, Math.Floor(HashD(i, 5.9) * 4));
            var dir = HashD(i, 7.7) < 0.5 ? 1 : -1;
            moves[i] = new Move(axis, lo, lo + 0.5, dir * Math.PI / 2);
        }
        return moves;
    }

    /// <summary>Globe: a lat/long field, a scan meridian sweeps (searching).</summary>
    private static void Globe(double size, double t, OrbOpts o, OrbFrame frame)
    {
        const double spin = 0.5;
        var cx = size / 2;
        var cy = size / 2;
        var radius = size / 2 * 0.82;
        var tilt = 0.4 + 0.06 * Math.Sin(t * 0.35);
        var pt = new Proj(t * spin, tilt, cx, cy, radius);
        var scan = t * (spin + (1.7 - spin) * o["scanMul", 1]);
        var rs = RadiusScale(size, o["rsPow", 0.6]);
        var dimBase = o["dimBase", 1];
        var latRings = (int)o["latRings", 17];
        var lonDensity = o["lonDensity", 44];
        for (var li = 0; li <= latRings; li++)
        {
            var lat = -Math.PI / 2 + (double)li / latRings * Math.PI;
            var cosLat = Math.Cos(lat);
            var sinLat = Math.Sin(lat);
            var lonCount = (int)Math.Max(1, Round(Math.Abs(cosLat) * lonDensity));
            for (var lj = 0; lj < lonCount; lj++)
            {
                var lon = (double)lj / lonCount * 2 * Math.PI;
                var (px, py, z) = pt.Of(cosLat * Math.Cos(lon), sinLat, cosLat * Math.Sin(lon));
                var depth = (z + 1) / 2;
                var d = AngleDelta(lon + t * spin, scan);
                var boost = Math.Exp(-(d * d) / 0.18) * Math.Max(0, z);
                frame.Dots.Add(new OrbDot(px, py, z,
                    (o["rBase", 0.6] + o["rDepth", 1.7] * depth + o["rBoost", 1] * boost) * rs,
                    o["inkFar", 0.62] - o["inkSpan", 0.54] * depth,
                    dimBase + (1 - dimBase) * Math.Min(1, boost)));
            }
        }
    }

    [ThreadStatic] private static double[]? _amount;
    [ThreadStatic] private static Move[]? _moves;

    /// <summary>Rubik: bands twist in quarter turns, scramble then solve (solving).</summary>
    private static void Rubik(double size, double t, OrbOpts o, OrbFrame frame)
    {
        var cx = size / 2;
        var cy = size / 2;
        var r = size / 2 * 0.82;
        var pt = new Proj(t * 0.55, 0.35 + 0.1 * Math.Sin(t * 0.9), cx, cy, r);
        var rs = RadiusScale(size, o["rsPow", 0.6]);
        var moveCount = (int)o["moveCount", 14];
        var moves = _moves is { } kept && kept.Length == moveCount ? kept : _moves = MakeMoves(moveCount);
        var amount = _amount is { } buffer && buffer.Length == moveCount ? buffer : _amount = new double[moveCount];
        var (_, active) = SolveCycle(t, moveCount, 0.42, 1.2, amount);
        var latRings = (int)o["latRings", 15];
        var lonDensity = o["lonDensity", 40];
        for (var li = 0; li <= latRings; li++)
        {
            var lat = -Math.PI / 2 + (double)li / latRings * Math.PI;
            var cosLat = Math.Cos(lat);
            var sinLat = Math.Sin(lat);
            var lonCount = (int)Math.Max(1, Round(Math.Abs(cosLat) * lonDensity));
            for (var lj = 0; lj < lonCount; lj++)
            {
                var lon = (double)lj / lonCount * 2 * Math.PI;
                var (x, y, z, inActive) = ApplyMoves(cosLat * Math.Cos(lon), sinLat, cosLat * Math.Sin(lon), moves, amount, active);
                var (px, py, zr) = pt.Of(x, y, z);
                var depth = (zr + 1) / 2;
                frame.Dots.Add(new OrbDot(px, py, zr,
                    (o["rBase", 0.6] + o["rDepth", 1.7] * depth + (inActive ? o["rActive", 0.3] : 0)) * rs,
                    o["inkFar", 0.62] - o["inkSpan", 0.54] * depth - (inActive ? 0.14 : 0)));
            }
        }
    }

    /// <summary>Wave: a waveform rolls through the rings (listening). Local hook: "swell" scales the wave's depth.</summary>
    private static void Wave(double size, double t, OrbOpts o, OrbFrame frame)
    {
        var cx = size / 2;
        var cy = size / 2;
        var r = size / 2 * 0.874;
        var pt = new Proj(t * 0.18, 0.38, cx, cy, 1);
        var rs = RadiusScale(size, o["rsPow", 0.6]);
        var swell = o["swell", 1];
        var rings = (int)o["rings", 15];
        var lonDensity = o["lonDensity", 40];
        for (var ri = 0; ri <= rings; ri++)
        {
            var lat = -Math.PI / 2 + (double)ri / rings * Math.PI;
            var cosLat = Math.Cos(lat);
            var sinLat = Math.Sin(lat);
            var w = 0.62 * Math.Sin(t * 2.1 - ri * 0.52) + 0.38 * Math.Sin(t * 1.27 + ri * 0.83);
            var rr = r * (0.88 + 0.105 * w * swell);
            var lonCount = (int)Math.Max(1, Round(Math.Abs(cosLat) * lonDensity));
            for (var lj = 0; lj < lonCount; lj++)
            {
                var lon = (double)lj / lonCount * 2 * Math.PI;
                var (px, py, z) = pt.Of(cosLat * Math.Cos(lon) * rr, sinLat * rr, cosLat * Math.Sin(lon) * rr);
                var depth = (z / r + 1) / 2;
                var crest = Math.Max(0, w);
                frame.Dots.Add(new OrbDot(px, py, z,
                    (o["rBase", 0.6] + o["rDepth", 1.7] * depth) * (1 + 0.4 * crest) * rs,
                    0.66 - 0.56 * depth - 0.1 * crest));
            }
        }
    }

    // ---- orbits.ts ---------------------------------------------------------------------------------------------------

    /// <summary>Orbits: particles on tilted orbits (working).</summary>
    private static void Orbits(double size, double t, OrbOpts o, OrbFrame frame)
    {
        var cx = size / 2;
        var cy = size / 2;
        var r = size / 2 * 0.82;
        var pt = new Proj(t * 0.12, 0.3, cx, cy, 1);
        var rs = RadiusScale(size, o["rsPow", 0.6]);
        var orbitN = (int)o["orbitN", 12];
        var ghostN = (int)o["ghostN", 40];
        var particles = (int)o["particles", 3];
        for (var orb = 0; orb < orbitN; orb++)
        {
            var h1 = HashD(orb, 1.7);
            var h2 = HashD(orb, 5.2);
            var h3 = HashD(orb, 8.9);
            var ro = r * (0.45 + 0.52 * h1);
            var th = h1 * 2 * Math.PI;
            var phi = Math.Acos(2 * h2 - 1);
            var nx = Math.Sin(phi) * Math.Cos(th);
            var ny = Math.Cos(phi);
            var nz = Math.Sin(phi) * Math.Sin(th);
            var ux = -ny;
            var uy = nx;
            const double uz = 0;
            var ul = Math.Max(1e-6, Math.Sqrt(ux * ux + uy * uy));
            ux /= ul;
            uy /= ul;
            var vx = ny * uz - nz * uy;
            var vy = nz * ux - nx * uz;
            var vz = nx * uy - ny * ux;
            var speed = (0.25 + 0.55 * h3) * (h3 > 0.5 ? 1 : -1);
            for (var k = 0; k < ghostN; k++)
            {
                var a = (double)k / ghostN * 2 * Math.PI;
                var (px, py, z) = pt.Of((ux * Math.Cos(a) + vx * Math.Sin(a)) * ro, (uy * Math.Cos(a) + vy * Math.Sin(a)) * ro,
                                        (uz * Math.Cos(a) + vz * Math.Sin(a)) * ro);
                var depth = (z / ro + 1) / 2;
                frame.Dots.Add(new OrbDot(px, py, z, o["ghostR", 0.9] * rs, 0.72, o["ghostA", 0.5] * (0.4 + 0.6 * depth)));
            }
            for (var m = 0; m < particles; m++)
            {
                var a = t * speed + (double)m / particles * 2 * Math.PI + h2 * 6;
                var (px, py, z) = pt.Of((ux * Math.Cos(a) + vx * Math.Sin(a)) * ro, (uy * Math.Cos(a) + vy * Math.Sin(a)) * ro,
                                        (uz * Math.Cos(a) + vz * Math.Sin(a)) * ro);
                var depth = (z / ro + 1) / 2;
                frame.Dots.Add(new OrbDot(px, py, z, (o["partR", 1.2] + o["partRDepth", 1.6] * depth) * rs, 0.3 - 0.22 * depth));
            }
        }
    }

    // ---- braid.ts -----------------------------------------------------------------------------------------------------

    /// <summary>Braid: three strands plait around the sphere (weaving).</summary>
    private static void Braid(double size, double t, OrbOpts o, OrbFrame frame)
    {
        var cx = size / 2;
        var cy = size / 2;
        var r = size / 2 * 0.76;
        var pt = new Proj(t * 0.4, 0.3, cx, cy, 1);
        var rs = RadiusScale(size, o["rsPow", 0.6]);
        var ghostN = (int)o["ghostN", 150];
        for (var i = 0; i < ghostN; i++)
        {
            var d = FibDir(i, ghostN);
            var (px, py, z) = pt.Of(d.X * r, d.Y * r, d.Z * r);
            var depth = (z / r + 1) / 2;
            frame.Dots.Add(new OrbDot(px, py, z, 0.8 * rs, 0.78, 0.1 + 0.22 * depth));
        }
        var strandN = (int)o["strandN", 52];
        var turns = o["turns", 3];
        for (var s = 0; s < 3; s++)
        {
            var phase = s / 3.0 * 2 * Math.PI;
            for (var i = 0; i < strandN; i++)
            {
                var u = (Frac((double)i / strandN + t * 0.045) * 2 - 1) * 0.96;
                var surf = Math.Sqrt(Math.Max(0, 1 - u * u));
                var endFade = Math.Min(1, (1 - Math.Abs(u)) / 0.1);
                var a = u * Math.PI * turns + phase;
                var weave = 1 + 0.075 * Math.Sin(u * Math.PI * turns * 2 + phase * 2 + t * 0.8);
                var rr = surf * r * weave;
                var (px, py, zr) = pt.Of(Math.Cos(a) * rr, u * r * weave, Math.Sin(a) * rr);
                var depth = (zr / r + 1) / 2;
                frame.Dots.Add(new OrbDot(px, py, zr, (o["rBase", 1.2] + o["rDepth", 1.8] * depth) * rs, 0.55 - 0.45 * depth,
                                          endFade * (0.45 + 0.55 * depth)));
            }
        }
    }

    // ---- web.ts ---------------------------------------------------------------------------------------------------------

    [ThreadStatic] private static (double X, double Y, double Z)[]? _nodes;

    /// <summary>Web: a constellation wires itself (connecting). Lines first, then nodes and signals.</summary>
    private static void Web(double size, double t, OrbOpts o, OrbFrame frame)
    {
        var cx = size / 2;
        var cy = size / 2;
        var r = size / 2 * 0.8 * o["spread", 1];
        var pt = new Proj(t * 0.12, 0.32, cx, cy, r);
        var rs = RadiusScale(size, o["rsPow", 0.6]);
        var nodeN = (int)o["nodeN", 30];
        var thr = o["thr", 0.72];
        var nodeR = o["nodeR", 1.4];
        var nodeRDepth = o["nodeRDepth", 1.8];
        var nodes = _nodes is { } kept && kept.Length == nodeN ? kept : _nodes = new (double, double, double)[nodeN];
        for (var i = 0; i < nodeN; i++)
        {
            var d = FibDir(i, nodeN);
            var x = d.X + 0.3 * (VNoise(i * 0.31 + 9, t * 0.24) - 0.5) * 2;
            var y = d.Y + 0.3 * (VNoise(i * 0.53 + 27, t * 0.21) - 0.5) * 2;
            var z = d.Z + 0.3 * (VNoise(i * 0.77 + 55, t * 0.27) - 0.5) * 2;
            var l = Math.Sqrt(x * x + y * y + z * z);
            nodes[i] = (x / l, y / l, z / l);
        }
        for (var i = 0; i < nodeN; i++)
        for (var j = i + 1; j < nodeN; j++)
        {
            var dx = nodes[i].X - nodes[j].X;
            var dy = nodes[i].Y - nodes[j].Y;
            var dz = nodes[i].Z - nodes[j].Z;
            var dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist >= thr) continue;
            var (x1, y1, z1) = pt.Of(nodes[i].X, nodes[i].Y, nodes[i].Z);
            var (x2, y2, z2) = pt.Of(nodes[j].X, nodes[j].Y, nodes[j].Z);
            var depth = ((z1 + z2) / 2 + 1) / 2;
            frame.Lines.Add(new OrbLine(x1, y1, x2, y2, 0.42, (1 - dist / thr) * (0.3 + 0.55 * depth),
                                        Math.Max(0.6, o["lineW", 0.8] * rs)));
        }
        for (var i = 0; i < nodeN; i++)
        {
            var (px, py, z) = pt.Of(nodes[i].X, nodes[i].Y, nodes[i].Z);
            var depth = (z + 1) / 2;
            var pulse = 1 + 0.25 * Math.Sin(t * 1.4 + i * 2.7);
            frame.Dots.Add(new OrbDot(px, py, z, (nodeR + nodeRDepth * depth) * pulse * rs, 0.55 - 0.45 * depth));
        }
        var signals = (int)o["signals", 5];
        for (var s = 0; s < signals; s++)
        {
            var seg = Math.Floor(t * 0.55 + s * 7.31);
            var a = (int)Math.Floor(HashD(seg, s * 3.1 + 1.7) * nodeN);
            var b = (int)Math.Floor(HashD(seg, s * 5.7 + 4.2) * nodeN);
            if (a == b) continue;
            var f = Frac(t * 0.55 + s * 7.31);
            var x = Lerp(nodes[a].X, nodes[b].X, f);
            var y = Lerp(nodes[a].Y, nodes[b].Y, f);
            var z = Lerp(nodes[a].Z, nodes[b].Z, f);
            var l = Math.Max(1e-6, Math.Sqrt(x * x + y * y + z * z));
            var (px, py, zr) = pt.Of(x / l, y / l, z / l);
            var depth = (zr + 1) / 2;
            frame.Dots.Add(new OrbDot(px, py, zr, (nodeR * 1.5 + nodeRDepth * depth) * rs, 0.05, 0.5 + 0.5 * depth));
        }
    }

    // ---- ribbon.ts ------------------------------------------------------------------------------------------------------

    /// <summary>Ribbon: an undulating sash of strands on a great circle (composing); with faceOn, a breathing ring.</summary>
    private static void Ribbon(double size, double t, OrbOpts o, OrbFrame frame)
    {
        var cx = size / 2;
        var cy = size / 2;
        var r = size / 2 * 0.78;
        var spin = o["spin", 1];
        const double camTilt = 0.3;
        var pt = new Proj(t * 0.1 * spin, camTilt, cx, cy, 1);
        var rs = RadiusScale(size, o["rsPow", 0.6]);
        var faceOn = o.Flag("faceOn");
        var ghostN = (int)o["ghostN", 150];
        for (var i = 0; i < ghostN; i++)
        {
            var d = FibDir(i, ghostN);
            var (px, py, z) = pt.Of(d.X * r, d.Y * r, d.Z * r);
            var depth = (z / r + 1) / 2;
            frame.Dots.Add(new OrbDot(px, py, z, 0.8 * rs, 0.78, 0.1 + 0.22 * depth));
        }
        var ya = t * 0.24 * spin;
        var ta = faceOn ? -camTilt : 0.55 + 0.3 * Math.Sin(t * 0.18) * spin;
        var ux = Math.Cos(ya);
        const double uy = 0;
        var uz = Math.Sin(ya);
        var vx = -uz * Math.Sin(ta);
        var vy = Math.Cos(ta);
        var vz = ux * Math.Sin(ta);
        var nx = uy * vz - uz * vy;
        var ny = uz * vx - ux * vz;
        var nz = ux * vy - uy * vx;
        var wobMul = o["wobMul", 1];
        var wobAmp = 0.23 * wobMul;
        var baseR = faceOn ? r / (1 + 0.85 * wobAmp) : r;
        var baseLanes = o["lanes", 5];
        var segs = (int)o["segs", 88];
        var lanes = (int)Math.Max(1, Round(baseLanes * o["bandMul", 1]));
        for (var w = 0; w < lanes; w++)
        {
            var laneOff = (w - (lanes - 1) / 2.0) * 0.075;
            var edge = Math.Abs(w - (lanes - 1) / 2.0) / Math.Max(1, (lanes - 1) / 2.0);
            for (var k = 0; k < segs; k++)
            {
                var a = (double)k / segs * 2 * Math.PI;
                var wob = (0.16 * Math.Sin(a * 3 - t * 1.7 + w * 0.22) + 0.07 * Math.Sin(a * 5 + t * 1.1)) * wobMul;
                var radial = faceOn ? 1 + wob : 1;
                var off = faceOn ? laneOff : laneOff + wob;
                var x = ux * Math.Cos(a) + vx * Math.Sin(a) + nx * off;
                var y = uy * Math.Cos(a) + vy * Math.Sin(a) + ny * off;
                var z = uz * Math.Cos(a) + vz * Math.Sin(a) + nz * off;
                var l = Math.Sqrt(x * x + y * y + z * z);
                var rr = baseR * radial;
                var (px, py, zr) = pt.Of(x / l * rr, y / l * rr, z / l * rr);
                var depth = (zr / r + 1) / 2;
                frame.Dots.Add(new OrbDot(px, py, zr, (o["rBase", 1.1] + o["rDepth", 1.7] * depth) * (1 - 0.25 * edge) * rs,
                                          0.52 - 0.44 * depth + 0.18 * edge, 0.4 + 0.6 * depth));
            }
        }
    }

    // ---- morph.ts -------------------------------------------------------------------------------------------------------

    private static double SmoothE(double x) => x * x * (3 - 2 * x);

    private delegate (double X, double Y) Path(double f);

    private static Path PolyPath((double X, double Y)[] verts)
    {
        var v = verts.Length;
        var lengths = new double[v];
        double total = 0;
        for (var i = 0; i < v; i++)
        {
            var a = verts[i];
            var b = verts[(i + 1) % v];
            lengths[i] = Hypot(b.X - a.X, b.Y - a.Y);
            total += lengths[i];
        }
        return f =>
        {
            var target = f * total;
            var i = 0;
            while (target > lengths[i] && i < v - 1)
            {
                target -= lengths[i];
                i++;
            }
            var a = verts[i];
            var b = verts[(i + 1) % v];
            var ff = lengths[i] != 0 ? Math.Min(1, target / lengths[i]) : 0;
            return (a.X + (b.X - a.X) * ff, a.Y + (b.Y - a.Y) * ff);
        };
    }

    /// <summary>JS Math.hypot.</summary>
    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    private static readonly Path Circle = f =>
    {
        var a = -Math.PI / 2 + f * 2 * Math.PI;
        return (Math.Cos(a) * 0.24, Math.Sin(a) * 0.24);
    };

    private static readonly Path Triangle = PolyPath([(0.0, -0.26), (0.24, 0.16), (-0.24, 0.16)]);
    private static readonly Path Square = PolyPath([(0, -0.2), (0.2, -0.2), (0.2, 0.2), (-0.2, 0.2), (-0.2, -0.2)]);
    private static readonly Path[] Cycle = [Circle, Triangle, Square];

    private static int MorphN(double d) => (int)Math.Max(6, Round(34 * d));

    private const double Hold = 1.4;
    private const double MorphTime = 0.9;
    private const double Seg = Hold + MorphTime;
    private const int Samples = 160;

    [ThreadStatic] private static (double X, double Y)[]? _morphPoints;
    [ThreadStatic] private static double[]? _morphLengths;

    /// <summary>Morph: a dotted outline cycling circle → triangle → square (shaping).</summary>
    private static void Morph(double size, double t, OrbOpts o, OrbFrame frame)
    {
        var count = Cycle.Length;
        var tc = t % (Seg * count);
        var k = (int)Math.Floor(tc / Seg);
        var local = tc - k * Seg;
        var m = local > Hold ? SmoothE((local - Hold) / MorphTime) : 0;
        var sprd = o["spread", 1];
        var pA = Cycle[k];
        var pB = Cycle[(k + 1) % count];
        var pts = _morphPoints ??= new (double, double)[Samples];
        var lengths = _morphLengths ??= new double[Samples];
        for (var i = 0; i < Samples; i++)
        {
            var f = (double)i / Samples;
            var a = pA(f);
            var b = pB(f);
            pts[i] = ((a.X + (b.X - a.X) * m) * sprd, (a.Y + (b.Y - a.Y) * m) * sprd);
        }
        double total = 0;
        for (var i = 0; i < Samples; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % Samples];
            lengths[i] = Hypot(b.X - a.X, b.Y - a.Y);
            total += lengths[i];
        }
        var n = MorphN(o["iconD", 1]);
        var re = o["rDot", 0.021] * 1.35 * sprd;
        var pulse = 1 + 0.02 * Math.Sin(local * 3.1);
        var c2 = size / 2;
        var seg = 0;
        double acc = 0;
        for (var k2 = 0; k2 < n; k2++)
        {
            var target = (double)k2 / n * total;
            while (acc + lengths[seg] < target && seg < Samples - 1)
            {
                acc += lengths[seg];
                seg++;
            }
            var a = pts[seg];
            var b = pts[(seg + 1) % Samples];
            var f = lengths[seg] != 0 ? Math.Min(1, (target - acc) / lengths[seg]) : 0;
            var x = (a.X + (b.X - a.X) * f) * pulse;
            var y = (a.Y + (b.Y - a.Y) * f) * pulse;
            frame.Dots.Add(new OrbDot(c2 + x * size, c2 + y * size, 0, Math.Max(0.35, re * size), 0.1));
        }
    }

    // ---- profiles.ts and presets.ts ----------------------------------------------------------------------------------

    private static readonly (string A, string B)[] CountPairs = [("latRings", "lonDensity"), ("rings", "lonDensity"), ("lanes", "segs")];
    private static readonly string[] CountKeys = ["orbitN", "ghostN", "nodeN", "strandN", "signals"];
    private static readonly string[] IconDensityKeys = ["iconD"];
    private static readonly string[] RadiusKeys = ["rBase", "rDepth", "rActive", "rDot", "ghostR", "partR", "partRDepth", "nodeR", "nodeRDepth"];

    public static Dictionary<string, double> ScaleCounts(Dictionary<string, double> opts, double scale)
    {
        var output = new Dictionary<string, double>(opts, StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var rt = Math.Sqrt(scale);
        foreach (var (a, b) in CountPairs)
        {
            if (output.TryGetValue(a, out var va) && output.TryGetValue(b, out var vb) && !done.Contains(a) && !done.Contains(b))
            {
                output[a] = Math.Max(2, Round(va * rt));
                output[b] = Math.Max(2, Round(vb * rt));
                done.Add(a);
                done.Add(b);
            }
        }
        foreach (var k in CountKeys)
            if (output.TryGetValue(k, out var v) && v != 0 && !done.Contains(k)) output[k] = Math.Max(1, Round(v * scale));
        foreach (var k in IconDensityKeys)
            if (output.TryGetValue(k, out var v)) output[k] = Math.Max(0.02, v * scale);
        return output;
    }

    public static Dictionary<string, double> ScaleRadii(Dictionary<string, double> opts, double scale)
    {
        var output = new Dictionary<string, double>(opts, StringComparer.Ordinal);
        foreach (var k in RadiusKeys)
            if (output.TryGetValue(k, out var v)) output[k] = v * scale;
        output["rSizeMul"] = (output.TryGetValue("rSizeMul", out var mul) ? mul : 1) * scale;
        return output;
    }

    private static Dictionary<string, double> Row(params (string Key, double Value)[] values)
    {
        var row = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, value) in values) row[key] = value;
        return row;
    }

    /// <summary>Base (fine) profiles per mode, before preset multipliers.</summary>
    public static readonly IReadOnlyDictionary<OrbMode, Dictionary<string, double>> BaseProfiles = new Dictionary<OrbMode, Dictionary<string, double>>
    {
        [OrbMode.Globe] = Row(("latRings", 17), ("lonDensity", 44), ("rBase", 0.6), ("rDepth", 1.7), ("rBoost", 1.0), ("inkFar", 0.62),
                              ("inkSpan", 0.54), ("rsPow", 0.6), ("rMin", 0.3)),
        [OrbMode.Orbits] = Row(("orbitN", 12), ("ghostN", 40), ("ghostR", 0.9), ("ghostA", 0.5), ("particles", 3), ("partR", 1.2),
                               ("partRDepth", 1.6), ("rsPow", 0.6), ("rMin", 0.3)),
        [OrbMode.Rubik] = Row(("latRings", 15), ("lonDensity", 40), ("moveCount", 14), ("rBase", 0.6), ("rDepth", 1.7), ("rActive", 0.3),
                              ("inkFar", 0.62), ("inkSpan", 0.54), ("rsPow", 0.6), ("rMin", 0.3)),
        [OrbMode.Wave] = Row(("rings", 15), ("lonDensity", 40), ("rBase", 0.6), ("rDepth", 1.7), ("rsPow", 0.6), ("rMin", 0.3)),
        [OrbMode.Web] = Row(("nodeN", 30), ("thr", 0.72), ("signals", 5), ("nodeR", 1.4), ("nodeRDepth", 1.8), ("lineW", 0.8),
                            ("rsPow", 0.6), ("rMin", 0.3)),
        [OrbMode.Braid] = Row(("strandN", 52), ("turns", 3.0), ("ghostN", 150), ("rBase", 1.2), ("rDepth", 1.8), ("rsPow", 0.6), ("rMin", 0.3)),
        [OrbMode.Ribbon] = Row(("lanes", 5), ("segs", 88), ("ghostN", 150), ("rBase", 1.1), ("rDepth", 1.7), ("rsPow", 0.6), ("rMin", 0.3)),
        [OrbMode.Ring] = Row(("lanes", 5), ("segs", 88), ("ghostN", 0), ("faceOn", 1), ("rBase", 1.1), ("rDepth", 1.7), ("rsPow", 0.6),
                             ("rMin", 0.3)),
        [OrbMode.Morph] = Row(("rDot", 0.021), ("iconD", 1), ("rMin", 0.25)),
    };

    public static OrbMode ModeOf(OrbState state) => state switch
    {
        OrbState.Working => OrbMode.Orbits,
        OrbState.Searching => OrbMode.Globe,
        OrbState.Solving => OrbMode.Rubik,
        OrbState.Listening => OrbMode.Wave,
        OrbState.Connecting => OrbMode.Web,
        OrbState.Weaving => OrbMode.Braid,
        OrbState.Composing => OrbMode.Ribbon,
        OrbState.Breathing => OrbMode.Ring,
        _ => OrbMode.Morph,
    };

    /// <summary>A shipped tuning: clock speed, count and radius multipliers, extra options merged after scaling.</summary>
    public readonly record struct Preset(double Speed, double Count, double Size, (string Key, double Value)[]? Extra = null);

    public static Preset PresetOf(OrbMode mode, int size)
    {
        var small = size == 20;
        return mode switch
        {
            OrbMode.Orbits => small ? new(3.9, 0.238, 2.4) : new(1.885, 1, 1),
            OrbMode.Globe => small ? new(2.665, 0.105, 1.75, [("scanMul", 4.335), ("dimBase", 0.45)])
                                   : new(2.015, 0.42, 1.15, [("scanMul", 4.08), ("dimBase", 0.45)]),
            OrbMode.Rubik => small ? new(1.95, 0.088, 1.9) : new(1.82, 0.35, 1.05),
            OrbMode.Wave => small ? new(3.998, 0.105, 1.6) : new(4.388, 0.341, 1),
            OrbMode.Web => small ? new(6.63, 0.25, 1.52) : new(3.315, 1.35, 0.95),
            OrbMode.Braid => small ? new(2.75, 0.1125, 1.36) : new(1.625, 0.5, 1),
            OrbMode.Ribbon => small ? new(3.12, 0.051, 1.073, [("spin", 0), ("bandMul", 4.94), ("wobMul", 1)])
                                    : new(2.34, 0.25, 0.85, [("spin", 0), ("bandMul", 3.9), ("wobMul", 1)]),
            OrbMode.Ring => small ? new(3.78, 0.028, 1.622, [("spin", 0), ("bandMul", 3.968), ("wobMul", 0.565)])
                                  : new(3.24, 0.25, 0.956, [("spin", 0), ("bandMul", 3.627), ("wobMul", 0.368)]),
            _ => small ? new(2.08, 0.53, 1.011, [("spread", 1.45)]) : new(2.405, 0.702, 0.395, [("spread", 1.45)]),
        };
    }

    private static readonly Dictionary<(OrbState, int), OrbResolved> Resolved = [];
    private static readonly Lock ResolvedLock = new();

    /// <summary>A (state, size) pair (size 64 or 20) resolved to its mode and fully scaled options; cached.</summary>
    public static OrbResolved Resolve(OrbState state, int size)
    {
        size = size == 20 ? 20 : 64;
        lock (ResolvedLock)
        {
            if (Resolved.TryGetValue((state, size), out var hit)) return hit;
            var mode = ModeOf(state);
            var preset = PresetOf(mode, size);
            var opts = new Dictionary<string, double>(BaseProfiles[mode], StringComparer.Ordinal);
            if (preset.Count != 1) opts = ScaleCounts(opts, preset.Count);
            if (preset.Size != 1) opts = ScaleRadii(opts, preset.Size);
            if (preset.Extra is { } extra)
                foreach (var (key, value) in extra) opts[key] = value;
            var resolved = new OrbResolved(mode, preset.Speed, new OrbOpts(opts));
            Resolved[(state, size)] = resolved;
            return resolved;
        }
    }

    /// <summary>The upstream state names, as stored ("listening", …).</summary>
    public static string Name(this OrbState state) => state switch
    {
        OrbState.Working => "working",
        OrbState.Searching => "searching",
        OrbState.Solving => "solving",
        OrbState.Listening => "listening",
        OrbState.Connecting => "connecting",
        OrbState.Weaving => "weaving",
        OrbState.Composing => "composing",
        OrbState.Breathing => "breathing",
        _ => "shaping",
    };

    public static readonly IReadOnlyList<OrbState> States =
        [OrbState.Working, OrbState.Searching, OrbState.Solving, OrbState.Listening, OrbState.Connecting, OrbState.Weaving,
         OrbState.Composing, OrbState.Breathing, OrbState.Shaping];

    public static OrbState? Parse(string? name)
    {
        foreach (var state in States)
            if (string.Equals(state.Name(), name?.Trim(), StringComparison.OrdinalIgnoreCase)) return state;
        return null;
    }
}
