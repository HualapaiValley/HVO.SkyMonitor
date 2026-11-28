namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>Gives hostless CameraAgent tests a disposable versioned store across process-scope restarts.</summary>
[TestClass]
[TestProperty("Area", "Hosting")]
public sealed class SiloFixtureAssemblyCleanup
{
    [AssemblyCleanup]
    public static void ReleaseTemporaryRoots() => SiloInfrastructureFixture.ReleaseTemporaryRoots();
}
