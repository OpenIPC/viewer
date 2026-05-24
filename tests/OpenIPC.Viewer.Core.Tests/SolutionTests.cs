namespace OpenIPC.Viewer.Core.Tests;

public sealed class SolutionTests
{
    [Fact]
    public void CoreAssembly_Loads()
    {
        // Placeholder sanity test until real Core entities arrive in Phase 1.
        var asm = typeof(global::OpenIPC.Viewer.Core.PhaseZeroMarker).Assembly;
        Assert.Equal("OpenIPC.Viewer.Core", asm.GetName().Name);
    }
}
