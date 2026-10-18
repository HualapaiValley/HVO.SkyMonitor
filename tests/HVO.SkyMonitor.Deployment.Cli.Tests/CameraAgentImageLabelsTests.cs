using System.Text.Json;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// #1314 R15: the label reader never throws on a label value that is not a string. The Docker API types labels as
/// strings, so such a value reads as absent, which leaves the image undeclared rather than failing a recovery path that
/// inspects it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentImageLabelsTests
{
    [TestMethod]
    public void ALabelThatIsNotAString_ReadsAsAbsentRatherThanThrowing()
    {
        using var labels = JsonDocument.Parse("""
            {
              "io.hvo.skymonitor.state-compatibility": 7,
              "io.hvo.skymonitor.raw-ingress-schema": null,
              "io.hvo.skymonitor.identity-migration": ["20260827053715_InitialIdentity"],
              "io.hvo.skymonitor.component": "cameraagent"
            }
            """);

        var identity = CameraAgentImageLabels.Identity("archive", "sha256:abc", "sha256:abc", "arm64", null, labels.RootElement);

        Assert.IsNull(identity.UpgradeCompatibility);
        Assert.IsNull(identity.RawIngressSchema);
        Assert.IsNull(identity.IdentityMigration);
        Assert.AreEqual("cameraagent", identity.Component);
    }
}
