using System.Runtime.InteropServices;

namespace Lunalyr.Tools.WallpaperCost;

/// <summary>
/// Windows' "GPU Engine" and "GPU Process Memory" performance counters for one process (what Task Manager's GPU columns
/// read; no admin rights needed), through pdh.dll. Sampled by the parent, so the measured process does none of this
/// work. Engine utilisation is summed per engine type over the process's engines on the measured adapter.
/// </summary>
internal sealed partial class GpuCounters : IDisposable
{
    private nint _query;
    private readonly nint _engines, _memory, _shared;
    private readonly string _pid;
    private readonly string _luid;
    private readonly List<(double ThreeD, double Decode, double Processing, double Other)> _samples = [];
    private readonly List<double> _dedicated = [], _sharedMb = [];

    public GpuCounters(int processId, string luid)
    {
        _pid = $"pid_{processId}_";
        _luid = luid;
        Check(PdhOpenQueryW(null, 0, out _query), "PdhOpenQuery");
        Check(PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _engines), "GPU Engine counter");
        Check(PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Dedicated Usage", 0, out _memory), "GPU Process Memory counter");
        Check(PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Shared Usage", 0, out _shared), "GPU Process Memory counter");
        // Utilisation is a rate: the first collection is its starting point.
        PdhCollectQueryData(_query);
    }

    /// <summary>Takes one sample (utilisation since the previous one).</summary>
    public void Sample()
    {
        if (PdhCollectQueryData(_query) != 0) return;
        double threeD = 0, decode = 0, processing = 0, other = 0;
        var any = false;
        foreach (var (name, value) in Read(_engines))
        {
            if (!name.StartsWith(_pid, StringComparison.Ordinal) || !name.Contains(_luid, StringComparison.OrdinalIgnoreCase)) continue;
            any = true;
            if (name.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) threeD += value;
            else if (name.EndsWith("engtype_VideoDecode", StringComparison.OrdinalIgnoreCase)) decode += value;
            else if (name.EndsWith("engtype_VideoProcessing", StringComparison.OrdinalIgnoreCase)) processing += value;
            else other += value;
        }
        if (any) _samples.Add((threeD, decode, processing, other));
        double dedicated = 0;
        foreach (var (name, value) in Read(_memory))
            if (name.StartsWith(_pid, StringComparison.Ordinal) && name.Contains(_luid, StringComparison.OrdinalIgnoreCase)) dedicated += value;
        _dedicated.Add(dedicated);
        double shared = 0;
        foreach (var (name, value) in Read(_shared))
            if (name.StartsWith(_pid, StringComparison.Ordinal) && name.Contains(_luid, StringComparison.OrdinalIgnoreCase)) shared += value;
        _sharedMb.Add(shared);
    }

    public int Samples => _samples.Count;

    public double ThreeD => _samples.Count == 0 ? double.NaN : _samples.Average(s => s.ThreeD);

    public double Decode => _samples.Count == 0 ? double.NaN : _samples.Average(s => s.Decode);

    public double Processing => _samples.Count == 0 ? double.NaN : _samples.Average(s => s.Processing);

    public double Other => _samples.Count == 0 ? double.NaN : _samples.Average(s => s.Other);

    /// <summary>Dedicated GPU memory at the last sample, in MB.</summary>
    public double DedicatedMb => _dedicated.Count == 0 ? double.NaN : _dedicated[^1] / (1024 * 1024);

    /// <summary>Shared (system) memory the GPU uses for the process at the last sample, in MB: an integrated GPU's own memory.</summary>
    public double SharedMb => _sharedMb.Count == 0 ? double.NaN : _sharedMb[^1] / (1024 * 1024);

    private static unsafe List<(string Name, double Value)> Read(nint counter)
    {
        var list = new List<(string, double)>();
        uint size = 0;
        var result = PdhGetFormattedCounterArrayW(counter, Format, ref size, out _, 0);
        if (result != MoreData || size == 0) return list;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, Format, ref size, out var count, buffer) != 0) return list;
            var items = (Item*)buffer;
            for (var i = 0; i < count; i++)
            {
                if (items[i].Status is not (0 or 1)) continue; // PDH_CSTATUS_VALID_DATA, PDH_CSTATUS_NEW_DATA
                list.Add((new string(items[i].Name), items[i].Value));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    public void Dispose()
    {
        if (_query != 0) PdhCloseQuery(_query);
        _query = 0;
    }

    private static void Check(uint status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"{what}: PDH error 0x{status:X8}");
    }

    private const uint Format = 0x00000200 | 0x00008000; // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100
    private const uint MoreData = 0x800007D2;

    /// <summary>PDH_FMT_COUNTERVALUE_ITEM_W with a double value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Item
    {
        public char* Name;
        public uint Status;
        public double Value;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
