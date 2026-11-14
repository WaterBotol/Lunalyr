using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Compositions.Stage;

/// <summary>Creates Hinge layers ("hinge"): heavy words swing open on a spine, page by page, and the book snaps shut.</summary>
public sealed class HingeFactory(IWordBreaker? breaker = null, Logger? logger = null) : IRenderLayerFactory
{
    public const string FactoryId = "hinge";
    public string Id => FactoryId;
    public IRenderLayer Create() => new HingeLayer(breaker ?? SimpleWordBreaker.Instance, logger);
}

/// <summary>Creates Broadside layers ("broadside"): one strip of big type that slides along, with little words as labels.</summary>
public sealed class BroadsideFactory(IWordBreaker? breaker = null, Logger? logger = null) : IRenderLayerFactory
{
    public const string FactoryId = "broadside";
    public string Id => FactoryId;
    public IRenderLayer Create() => new BroadsideLayer(breaker ?? SimpleWordBreaker.Instance, logger);
}

/// <summary>Creates Portal layers ("portal"): the sung word in a pill; earlier words drift away as pale ones.</summary>
public sealed class PortalFactory(IWordBreaker? breaker = null, Logger? logger = null) : IRenderLayerFactory
{
    public const string FactoryId = "portal";
    public string Id => FactoryId;
    public IRenderLayer Create() => new PortalLayer(breaker ?? SimpleWordBreaker.Instance, logger);
}

/// <summary>Creates Mosaic layers ("mosaic"): a collage, each word set huge, on a colour bar, or letter by letter.</summary>
public sealed class MosaicFactory(IWordBreaker? breaker = null, Logger? logger = null) : IRenderLayerFactory
{
    public const string FactoryId = "mosaic";
    public string Id => FactoryId;
    public IRenderLayer Create() => new MosaicLayer(breaker ?? SimpleWordBreaker.Instance, logger);
}

/// <summary>Creates Torque layers ("torque"): a turning column of extruded type, a face per phrase.</summary>
public sealed class TorqueFactory(IWordBreaker? breaker = null, Logger? logger = null) : IRenderLayerFactory
{
    public const string FactoryId = "torque";
    public string Id => FactoryId;
    public IRenderLayer Create() => new TorqueLayer(breaker ?? SimpleWordBreaker.Instance, logger);
}

/// <summary>Creates Fable layers ("fable"): a storybook page per line, each word landing by its role.</summary>
public sealed class FableFactory(IWordBreaker? breaker = null, Logger? logger = null) : IRenderLayerFactory
{
    public const string FactoryId = "fable";
    public string Id => FactoryId;
    public IRenderLayer Create() => new FableLayer(breaker ?? SimpleWordBreaker.Instance, logger);
}

/// <summary>The six stage layouts' factories, registered under the Mac's ids.</summary>
public static class StageFactories
{
    public static IRenderLayerFactory[] All(IWordBreaker? breaker = null, Logger? logger = null) =>
    [
        new HingeFactory(breaker, logger), new BroadsideFactory(breaker, logger), new PortalFactory(breaker, logger),
        new MosaicFactory(breaker, logger), new TorqueFactory(breaker, logger), new FableFactory(breaker, logger),
    ];
}
