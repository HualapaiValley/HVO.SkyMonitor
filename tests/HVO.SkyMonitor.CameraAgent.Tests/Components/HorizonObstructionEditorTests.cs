using System.Diagnostics.CodeAnalysis;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.OpticalCalibration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class HorizonObstructionEditorTests
{
    [TestMethod]
    public void PreviewDoesNotSaveAndEditsInvalidateTheReviewedCandidate()
    {
        var service = new Mock<ICameraAgentNamedRigUiService>();
        var active = Active();
        var preview = Preview(active);
        service.Setup(s => s.PreviewHorizonAsync(active.RevisionId, 1, "installation", 30,
            It.IsAny<IReadOnlyList<HorizonObstructionSample>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<HorizonObstructionPreview>.Success(preview));
        using var context = new BunitContext();
        context.Services.AddSingleton(service.Object);
        var cut = context.Render<HorizonObstructionEditor>(p => p.Add(c => c.ActiveRevision, active).Add(c => c.SelectionVersion, 1));
        cut.Find("#horizon-installation").Input("installation");
        cut.Find("#horizon-samples").Input("0,20\n10,?\n20,30");
        cut.Find("#horizon-preview").Click();
        cut.WaitForElement("#horizon-save");
        Assert.IsNotNull(cut.Find("svg[role='img']"));
        service.Verify(s => s.SaveHorizonAsync(It.IsAny<HorizonObstructionPreview>(), It.IsAny<CancellationToken>()), Times.Never);
        cut.Find("#horizon-samples").Input("0,21");
        Assert.IsEmpty(cut.FindAll("#horizon-save"));
    }

    [TestMethod]
    public void ExplicitSaveSelectsCandidateWithoutStagingOrRestarting()
    {
        var service = new Mock<ICameraAgentNamedRigUiService>();
        var active = Active();
        var preview = Preview(active);
        service.Setup(s => s.PreviewHorizonAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<double>(),
            It.IsAny<IReadOnlyList<HorizonObstructionSample>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<HorizonObstructionPreview>.Success(preview));
        service.Setup(s => s.SaveHorizonAsync(preview, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<NamedRigRevision>.Success(active with { RevisionId = "candidate", Rig = active.Rig with { HorizonObstruction = preview.Profile } }));
        using var context = new BunitContext();
        context.Services.AddSingleton(service.Object);
        string? selected = null;
        var cut = context.Render<HorizonObstructionEditor>(p => p.Add(c => c.ActiveRevision, active)
            .Add(c => c.SelectionVersion, 1).Add(c => c.Saved, (string id) => selected = id));
        cut.Find("#horizon-preview").Click();
        cut.WaitForElement("#horizon-save").Click();
        cut.WaitForAssertion(() => Assert.AreEqual("candidate", selected));
        service.Verify(s => s.RequestRestartAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.IsNull(active.Rig.HorizonObstruction);
    }

    private static NamedRigRevision Active()
    {
        var config = VirtualOpticalCalibrationReviewServiceTests.Configuration("VirtualSky");
        return new("active", "rig", 1, "camera", "optics", "mount", config.Module, config.Rig, null);
    }

    private static HorizonObstructionPreview Preview(NamedRigRevision active) => new(active.RevisionId, 1,
        new(HorizonObstructionProfile.CurrentSchemaVersion, "profile", 2, "installation", new string('A', 64),
            HorizonObstructionProfile.ComputeGeometryIdentitySha256(active.Rig), HorizonObstructionSource.Manual, 30,
            [new(0, 20), new(10, null), new(20, 30)]), [new(0, 20), new(1, 20), new(2, null)]);
}
