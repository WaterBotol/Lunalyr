// Scattered, Constellation, Constellation + Editorial and Echo: whole lines set as Editorial sets them (the Mac draws
// all four with ScatteredLyricText), with their own typography, links and repeats. See docs/compositions/scattered.md.

using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Compositions;

/// <summary>A composition that sets each word-timed line whole: the stage's shared drawing, steered by its traits.</summary>
internal sealed class LineSetLayer(CompositionKind kind, IWordBreaker breaker, Logger? logger)
    : LyricCompositionLayer(kind, breaker, logger);

/// <summary>Creates a whole-line composition's layers under its Mac id.</summary>
public abstract class LineSetFactory(CompositionKind kind, IWordBreaker? breaker, Logger? logger) : IRenderLayerFactory
{
    public CompositionKind Kind { get; } = kind;
    public string Id => Kind.Id();
    public IRenderLayer Create() => new LineSetLayer(Kind, breaker ?? SimpleWordBreaker.Instance, logger);
}

/// <summary>Creates Scattered layers ("scattered"): words set loose across the line.</summary>
public sealed class ScatteredFactory(IWordBreaker? breaker = null, Logger? logger = null)
    : LineSetFactory(CompositionKind.Scattered, breaker, logger)
{
    public const string FactoryId = "scattered";
}

/// <summary>Creates Constellation layers ("constellation"): fragments linked by hairlines, like a star chart.</summary>
public sealed class ConstellationFactory(IWordBreaker? breaker = null, Logger? logger = null)
    : LineSetFactory(CompositionKind.Constellation, breaker, logger)
{
    public const string FactoryId = "constellation";
}

/// <summary>Creates Constellation + Editorial layers ("constellationEditorial"): connected fragments with Editorial's contrasting typefaces.</summary>
public sealed class ConstellationEditorialFactory(IWordBreaker? breaker = null, Logger? logger = null)
    : LineSetFactory(CompositionKind.ConstellationEditorial, breaker, logger)
{
    public const string FactoryId = "constellationEditorial";
}

/// <summary>Creates Echo layers ("echo"): the phrase repeated at many sizes, some inverted, behind the live line.</summary>
public sealed class EchoFactory(IWordBreaker? breaker = null, Logger? logger = null)
    : LineSetFactory(CompositionKind.Echo, breaker, logger)
{
    public const string FactoryId = "echo";
}
