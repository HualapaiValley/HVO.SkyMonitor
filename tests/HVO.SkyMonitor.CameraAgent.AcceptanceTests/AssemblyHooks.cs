using System.Diagnostics.CodeAnalysis;

[assembly: DoNotParallelize]

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class AssemblyHooks
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }
}
