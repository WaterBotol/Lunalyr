namespace Lunalyr.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreAssemblyLoads() => Assert.Equal("Lunalyr.Core", typeof(Lunalyr.Core.AssemblyMarker).Assembly.GetName().Name);
}
