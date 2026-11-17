using Lunalyr.Core.Compositions;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>Echo's repeats drift on their phrase run's path (the Mac's ddb1a3c5): one path through a run, not one per line.</summary>
public sealed class EchoRunDriftTests
{
    [Fact]
    public void LinesOfOneRunDriftTheirRepeatsOnOnePath()
    {
        const long run = 917;
        for (var slot = 0; slot < EchoBackdrop.Repeats.Count; slot++)
        {
            var first = EchoBackdrop.Pose(slot, 4, 30, 50, false, 60, driftSeed: run);
            var second = EchoBackdrop.Pose(slot, 7, 30, 50, false, 60, driftSeed: run);
            // Each line keeps its own resting place (which side it leans to), but the wander is the run's.
            Assert.Equal(first.X - first.RestX, second.X - second.RestX, 9);
            Assert.Equal(first.Y - first.RestY, second.Y - second.RestY, 9);
        }
        // Without a run each line wanders on its own.
        var own = EchoBackdrop.Pose(0, 4, 30, 50, false, 60);
        var other = EchoBackdrop.Pose(0, 6, 30, 50, false, 60);
        Assert.NotEqual(own.X - own.RestX, other.X - other.RestX);
    }
}
