using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Compositions;

/// <summary>Creates Cascade layers ("cascade"): a moving column of words, arriving with the voice.</summary>
/// <param name="breaker">Parts unspaced lines into the words they roll as (the system's word breaker; a simple one when null).</param>
/// <param name="logger">Where a layer reports a frame it could not draw.</param>
public sealed class CascadeFactory(IWordBreaker? breaker = null, Logger? logger = null) : IRenderLayerFactory
{
    public const string FactoryId = "cascade";
    public string Id => FactoryId;
    public IRenderLayer Create() => new SpokenLayer(breaker ?? SimpleWordBreaker.Instance, logger, CompositionKind.Cascade);
}

/// <summary>Creates Cutout layers ("cutout"): uneven type and album-colour labels, built word by word.</summary>
/// <param name="breaker">Parts unspaced lines into the words they roll as (the system's word breaker; a simple one when null).</param>
/// <param name="logger">Where a layer reports a frame it could not draw.</param>
public sealed class CutoutFactory(IWordBreaker? breaker = null, Logger? logger = null) : IRenderLayerFactory
{
    public const string FactoryId = "cutout";
    public string Id => FactoryId;
    public IRenderLayer Create() => new SpokenLayer(breaker ?? SimpleWordBreaker.Instance, logger, CompositionKind.Cutout);
}
