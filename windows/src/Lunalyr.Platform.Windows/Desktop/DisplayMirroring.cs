using static Lunalyr.Platform.Desktop.FloatingNative;

namespace Lunalyr.Platform.Desktop;

/// <summary>
/// Whether Windows mirrors a display now: the Windows counterpart of the Mac's <c>CGDisplayIsInMirrorSet</c>. When
/// Windows duplicates its desktop (Win+P › Duplicate, "Duplicate these displays", a projector or a TV, a wireless
/// display), one source (the same picture) drives two or more monitors, so DisplayConfig lists two active paths with
/// the same source. Read only; nothing is changed.
/// </summary>
public static unsafe class DisplayMirroring
{
    /// <summary>
    /// Any source shown on more than one monitor (the Mac's <c>anyMirrored</c>): <paramref name="sources"/> are the
    /// active paths' sources, each its adapter and the source's id on it.
    /// </summary>
    public static bool AnyMirrored(IEnumerable<(long Adapter, uint Source)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var seen = new HashSet<(long, uint)>();
        foreach (var source in sources)
            if (!seen.Add(source)) return true;
        return false;
    }

    /// <summary>Whether a display is mirrored now; null when DisplayConfig can't be read (nothing is assumed then).</summary>
    public static bool? Now()
    {
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0) return null;
            if (pathCount == 0) return false;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[Math.Max(1, modeCount)];
            fixed (DISPLAYCONFIG_PATH_INFO* p = paths)
            fixed (DISPLAYCONFIG_MODE_INFO* m = modes)
            {
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, p, ref modeCount, m, 0) != 0) return null;
            }
            var sources = new (long, uint)[pathCount];
            for (var i = 0; i < pathCount; i++)
            {
                var source = paths[i].sourceInfo;
                sources[i] = (((long)source.adapterId.HighPart << 32) | source.adapterId.LowPart, source.id);
            }
            return AnyMirrored(sources);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
