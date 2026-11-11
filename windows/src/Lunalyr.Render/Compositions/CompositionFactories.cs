using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Compositions;

/// <summary>Creates Editorial layers ("editorial"): two typefaces in one line, set like a spread.</summary>
/// <param name="breaker">Parts unspaced lines into words (the system's word breaker; a simple one when null).</param>
/// <param name="logger">Where a layer reports a frame it could not draw.</param>
public sealed class EditorialFactory(IWordBreaker? breaker = null, Logger? logger = null) : IRenderLayerFactory
{
    public const string FactoryId = "editorial";
    public string Id => FactoryId;
    public IRenderLayer Create() => new EditorialLayer(breaker ?? SimpleWordBreaker.Instance, logger);
}

/// <summary>Creates Spoken layers ("spoken"): each sung word takes focus; recent words settle around it.</summary>
/// <param name="breaker">Parts unspaced lines into the words they roll as (the system's word breaker; a simple one when null).</param>
/// <param name="logger">Where a layer reports a frame it could not draw.</param>
public sealed class SpokenFactory(IWordBreaker? breaker = null, Logger? logger = null) : IRenderLayerFactory
{
    public const string FactoryId = "spoken";
    public string Id => FactoryId;
    public IRenderLayer Create() => new SpokenLayer(breaker ?? SimpleWordBreaker.Instance, logger);
}
