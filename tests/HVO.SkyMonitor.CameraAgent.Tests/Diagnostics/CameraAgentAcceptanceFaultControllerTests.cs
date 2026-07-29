using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Diagnostics;

namespace HVO.SkyMonitor.CameraAgent.Tests.Diagnostics;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentAcceptanceFaultControllerTests
{
    [TestMethod]
    public void CalibrationFaultUsesRelativePathAsTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-acceptance-faults", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string operationId = "calibration-target";
            const string relativePath = "calibration/bias.fit";
            File.WriteAllText(
                Path.Combine(root, "arm.json"),
                $$"""
                {
                  "operationId": "{{operationId}}",
                  "boundary": "calibration.BeforePayloadWrite",
                  "action": "throw",
                  "nodeId": "{{relativePath}}"
                }
                """);
            var controller = new CameraAgentAcceptanceFaultController(root);

            Assert.ThrowsExactly<IOException>(() => controller.Inject(
                CalibrationPublicationFaultPoint.BeforePayloadWrite,
                relativePath));

            using var hit = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, $"hit-{operationId}.json")));
            Assert.AreEqual(relativePath, hit.RootElement.GetProperty("nodeId").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
