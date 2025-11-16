using System.Threading.Tasks;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Assembly-level hooks to bootstrap the shared integration test fixture.
/// </summary>
[TestClass]
public sealed class AssemblyHooks
{
    internal static IntegrationTestFixture Fixture { get; private set; } = null!;

    [AssemblyInitialize]
    public static async Task AssemblyInitialize(TestContext context)
    {
        Fixture = new IntegrationTestFixture();
        await Fixture.InitializeAsync();
    }

    [AssemblyCleanup]
    public static void AssemblyCleanup()
    {
        Fixture.Dispose();
    }
}
