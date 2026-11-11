namespace Lunalyr.Platform;

/// <summary>Anchor type for tests and reflection-free assembly lookups.</summary>
public static class AssemblyMarker
{
    // Touch Core so the reference is kept (and the smoke test can see it).
    internal static readonly Type CoreAnchor = typeof(Lunalyr.Core.AssemblyMarker);
}
