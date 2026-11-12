using Lunalyr.Core.Backgrounds;

namespace Lunalyr.Shell.Tests;

/// <summary>What "Match the music" may draw now the GPU effects and line patterns are in the catalogue.</summary>
internal static class TestBackgrounds
{
    /// <summary>Whether <paramref name="layerId"/> is one of this build's GPU effects or line patterns.</summary>
    public static bool IsGpu(string layerId) =>
        GpuEffectCatalog.FromLayerId(layerId) is not null || LinePatternCatalog.FromLayerId(layerId) is not null;
}
