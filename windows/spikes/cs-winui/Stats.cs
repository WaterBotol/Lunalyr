using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LunalyrSpike;

internal sealed class Stats
{
    readonly List<double> _cpu = new(100_000), _interval = new(100_000);
    readonly object _lock = new();
    public readonly List<string> Notes = [];
    public double StartupMs;
    readonly Stopwatch _wall = Stopwatch.StartNew();
    TimeSpan _cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;

    public void ResetWindow()
    {
        lock (_lock) { _cpu.Clear(); _interval.Clear(); }
        _wall.Restart();
        _cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;
    }

    public void AddFrame(double cpuMs, double intervalMs)
    {
        lock (_lock)
        {
            _cpu.Add(cpuMs);
            if (!double.IsNaN(intervalMs)) _interval.Add(intervalMs);
        }
    }

    static JsonObject Pct(List<double> v)
    {
        if (v.Count == 0) return new JsonObject { ["avg"] = 0.0, ["p50"] = 0.0, ["p95"] = 0.0, ["p99"] = 0.0 };
        var s = v.OrderBy(x => x).ToArray();
        double P(double q) => s[Math.Min(s.Length - 1, (int)(q * s.Length))];
        return new JsonObject { ["avg"] = Math.Round(s.Average(), 3), ["p50"] = Math.Round(P(0.5), 3), ["p95"] = Math.Round(P(0.95), 3), ["p99"] = Math.Round(P(0.99), 3) };
    }

    public void Write(string path, string adapter, IEnumerable<JsonObject> monitors, int refreshHz)
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        double wallS = _wall.Elapsed.TotalSeconds;
        double cpuS = (p.TotalProcessorTime - _cpuAtStart).TotalSeconds;
        double refreshMs = 1000.0 / Math.Max(1, refreshHz);
        JsonObject result;
        lock (_lock)
        {
            result = new JsonObject
            {
                ["stack"] = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "cs-winui-d2d" : "cs-winui-d2d-aot",
                ["adapter"] = adapter,
                ["monitors"] = new JsonArray(monitors.Select(m => (JsonNode)m.DeepClone()).ToArray()),
                ["seconds"] = Math.Round(wallS, 2),
                ["frames"] = _cpu.Count,
                ["fps"] = Math.Round(_cpu.Count / wallS, 1),
                ["cpuMsPerFrame"] = Pct(_cpu),
                ["frameIntervalMs"] = Pct(_interval),
                ["slowFrames"] = _interval.Count(x => x > refreshMs * 1.5),
                ["startupMs"] = Math.Round(StartupMs, 1),
                ["workingSetMB"] = Math.Round(p.WorkingSet64 / 1048576.0, 1),
                ["peakWorkingSetMB"] = Math.Round(p.PeakWorkingSet64 / 1048576.0, 1),
                ["privateMB"] = Math.Round(p.PrivateMemorySize64 / 1048576.0, 1),
                ["processCpuPercent"] = Math.Round(cpuS / wallS / Environment.ProcessorCount * 100, 2),
                ["notes"] = new JsonArray(Notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}

internal static class Png
{
    public static unsafe void SaveBgra(string path, nint data, int w, int h, int pitch)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var raw = new byte[h * (w * 4 + 1)];
        byte* src = (byte*)data;
        for (int y = 0; y < h; y++)
        {
            int o = y * (w * 4 + 1);
            raw[o] = 0;
            byte* row = src + (long)y * pitch;
            for (int x = 0; x < w; x++)
            {
                raw[o + 1 + x * 4] = row[x * 4 + 2];
                raw[o + 2 + x * 4] = row[x * 4 + 1];
                raw[o + 3 + x * 4] = row[x * 4 + 0];
                raw[o + 4 + x * 4] = 255;
            }
        }
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)w); WriteBE(ihdr, 4, (uint)h);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(fs, "IHDR", ihdr);
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(raw);
            Chunk(fs, "IDAT", ms.ToArray());
        }
        Chunk(fs, "IEND", []);
    }

    static void WriteBE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, (uint)data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t);
        s.Write(data);
        uint crc = Crc(t, data);
        var c = new byte[4]; WriteBE(c, 0, crc); s.Write(c);
    }

    static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFF;
        foreach (var x in a) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
