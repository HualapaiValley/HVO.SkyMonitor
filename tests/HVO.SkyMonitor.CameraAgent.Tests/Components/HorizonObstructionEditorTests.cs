using System.Diagnostics.CodeAnalysis;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
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

    [TestMethod]
    public void PreviewRendersFractionalPeakAndKeepsUnsupportedIntervalsGray()
    {
        var active = Active();
        var profile = Preview(active).Profile;
        profile = new(profile.SchemaVersion, profile.ProfileId, profile.Revision, profile.InstallationId,
            HorizonObstructionProfile.ComputeSiteIdentitySha256(0, 0, 0), profile.GeometryIdentitySha256, profile.Source, .5,
            [new(0, -10), new(.5, 80), new(1, -10), new(1.25, null), new(2, 20), new(3, 20)]);
        var evaluator = new HorizonObstructionEvaluator(profile, active.Rig, new(0, 0, 0));
        var preview = new HorizonObstructionPreview(active.RevisionId, 1, profile, evaluator.KnownIntervals());
        var service = new Mock<ICameraAgentNamedRigUiService>();
        service.Setup(s => s.PreviewHorizonAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<double>(),
            It.IsAny<IReadOnlyList<HorizonObstructionSample>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<HorizonObstructionPreview>.Success(preview));
        using var context = new BunitContext();
        context.Services.AddSingleton(service.Object);
        var cut = context.Render<HorizonObstructionEditor>(p => p.Add(c => c.ActiveRevision, active).Add(c => c.SelectionVersion, 1));
        cut.Find("#horizon-preview").Click();
        cut.WaitForElement("#horizon-save");
        Assert.HasCount(4, cut.FindAll("polygon.clear-sky"));
        foreach (var polygon in cut.FindAll("polygon.clear-sky"))
            Assert.IsTrue(polygon.GetAttribute("points")!.Split(' ').All(point =>
                double.Parse(point.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture) <= 90),
                "Below geometric zero is never painted clear, even for a negative terrain elevation.");
        Assert.IsTrue(cut.FindAll("line.horizon").Any(line => line.GetAttribute("x2") == "0.5" && line.GetAttribute("y2") == "10"));
        Assert.HasCount(5, cut.FindAll("circle.horizon"), "Isolated known knots are points, never painted clear intervals.");
    }

    [TestMethod]
    [DataRow("samples")]
    [DataRow("installation")]
    [DataRow("gap")]
    [DataRow("selection")]
    public async Task DelayedPreviewCannotRestoreSaveAfterFormOrSelectionChanges(string change)
    {
        var active = Active();
        var pending = new TaskCompletionSource<OperatorUiResult<HorizonObstructionPreview>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<ICameraAgentNamedRigUiService>();
        service.Setup(s => s.PreviewHorizonAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<double>(),
            It.IsAny<IReadOnlyList<HorizonObstructionSample>>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<OperatorUiResult<HorizonObstructionPreview>>(pending.Task));
        using var context = new BunitContext();
        context.Services.AddSingleton(service.Object);
        var cut = context.Render<HorizonObstructionEditor>(p => p.Add(c => c.ActiveRevision, active).Add(c => c.SelectionVersion, 1));
        await cut.Find("#horizon-samples").InputAsync(new() { Value = "0,20\n20,20" }).ConfigureAwait(false);
        var click = cut.Find("#horizon-preview").ClickAsync(new());
        cut.WaitForAssertion(() => service.Verify(s => s.PreviewHorizonAsync(It.IsAny<string>(), 1, It.IsAny<string>(), It.IsAny<double>(),
            It.IsAny<IReadOnlyList<HorizonObstructionSample>>(), It.IsAny<CancellationToken>()), Times.Once));
        switch (change)
        {
            case "samples": await cut.Find("#horizon-samples").InputAsync(new() { Value = "0,80\n20,80" }).ConfigureAwait(false); break;
            case "installation": await cut.Find("#horizon-installation").InputAsync(new() { Value = "moved" }).ConfigureAwait(false); break;
            case "gap": await cut.Find("#horizon-gap").ChangeAsync(new() { Value = "15" }).ConfigureAwait(false); break;
            case "selection": await cut.InvokeAsync(() => cut.Render(p => p.Add(c => c.ActiveRevision, active).Add(c => c.SelectionVersion, 2))).ConfigureAwait(false); break;
        }
        pending.SetResult(OperatorUiResult<HorizonObstructionPreview>.Success(Preview(active)));
        await click.ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#horizon-preview").HasAttribute("disabled")));
        Assert.IsEmpty(cut.FindAll("#horizon-save"));
        Assert.DoesNotContain("Preview ready", cut.Markup);
        service.Verify(s => s.SaveHorizonAsync(It.IsAny<HorizonObstructionPreview>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public void ActiveHorizonShowsRevalidationWarning()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton(Mock.Of<ICameraAgentNamedRigUiService>());
        var cut = context.Render<HorizonObstructionEditor>(p => p.Add(c => c.ActiveRevision, Active())
            .Add(c => c.Compatibility, "Horizon revalidation required. Detection is quarantined."));
        Assert.Contains("revalidation required", cut.Find("#horizon-compatibility").TextContent);
    }

    private static NamedRigRevision Active()
    {
        var config = VirtualOpticalCalibrationReviewServiceTests.Configuration("VirtualSky");
        return new("active", "rig", 1, "camera", "optics", "mount", config.Module, config.Rig, null);
    }

    private static HorizonObstructionPreview Preview(NamedRigRevision active) => new(active.RevisionId, 1,
        new(HorizonObstructionProfile.CurrentSchemaVersion, "profile", 2, "installation", new string('A', 64),
            HorizonObstructionProfile.ComputeGeometryIdentitySha256(active.Rig), HorizonObstructionSource.Manual, 30,
            [new(0, 20), new(10, null), new(20, 30)]), []);
}
