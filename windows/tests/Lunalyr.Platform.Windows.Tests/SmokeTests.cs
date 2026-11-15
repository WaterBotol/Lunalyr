namespace Lunalyr.Platform.Windows.Tests;

public class SmokeTests
{
    [Fact]
    public void PlatformAssemblyReferencesCore() =>
        Assert.Contains(typeof(Lunalyr.Platform.AssemblyMarker).Assembly.GetReferencedAssemblies(), a => a.Name == "Lunalyr.Core");
}
