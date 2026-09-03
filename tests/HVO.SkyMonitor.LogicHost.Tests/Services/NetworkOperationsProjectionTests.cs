using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class NetworkOperationsProjectionTests
{
    [TestMethod]
    public void OperationsProjectionComposesCompleteDerivativeAndEnvironmentTrace()
    {
        var now = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        var observatoryId = Guid.NewGuid();
        var cameraId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var captureId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var role = ObservatoryMembershipRole.Owner;
        var observatory = new OperationsObservatorySummary(
            observatoryId, "Observatory", role, 1, 1, 0, 1);
        var environment = new OperationsEnvironmentalSummary(
            EnvironmentalObservationKind.AirTemperature,
            EnvironmentalObservationUnit.DegreesCelsius,
            5,
            null,
            EnvironmentalObservationQuality.Good,
            now,
            now.AddMinutes(5));
        var installation = new OperationsCameraInstallationSummary(
            installationId,
            "Installation",
            DeviceRegistrationStatus.Active,
            now,
            null,
            null,
            "installed",
            null);
        var camera = new OperationsCameraFleetSummary(
            cameraId,
            "Camera",
            "Description",
            installationId,
            installation.InstallationName,
            FleetHealth.Healthy,
            now,
            false,
            false,
            false,
            "1.0.0",
            true);
        var registration = new OperationsRegistrationSummary(
            Guid.NewGuid(), "Agent", DeviceRegistrationStatus.Active, cameraId);
        var observatoryDetail = new OperationsObservatoryDetail(
            observatory, [camera], null, [environment], [registration], null);
        var cameraDetail = new OperationsCameraDetail(cameraId, observatoryId, camera.Name, camera.Description,
            [installation], null);

        var capture = new OperationsCaptureSummary(
            captureId,
            observatoryId,
            cameraId,
            installationId,
            installationId,
            observatory.Name,
            camera.Name,
            now,
            now.AddSeconds(1),
            "rig",
            1,
            Guid.NewGuid(),
            42,
            1,
            true,
            true,
            false);
        var artifact = new OperationsArtifactSummary(
            artifactId,
            artifactId,
            FrameArtifactRole.Preview,
            "graph",
            "1",
            "image/jpeg",
            100,
            new string('A', 64),
            CentralArtifactObjectState.Available,
            CentralReconstructionState.Complete,
            null,
            now,
            "/content",
            "graph",
            1,
            "preview",
            "1.0.0",
            new string('B', 64),
            "manifest-v2",
            true,
            true);
        var profile = new OperationsCaptureProfileSummary(
            CentralProfileKind.Rig, "camera", "1", new string('C', 64));
        var provenance = new OperationsCaptureProvenanceSummary(
            "agent",
            Guid.NewGuid(),
            CentralCaptureLocationEvidenceState.ReportedResolved,
            "location",
            1,
            now,
            now,
            now.AddSeconds(1),
            now.AddSeconds(2),
            now.AddSeconds(3),
            TimeSpan.FromSeconds(1).Ticks,
            10,
            5,
            -10,
            [profile]);
        var lineage = new OperationsArtifactLineageSummary(
            artifactId, 0, artifactId, artifactId, artifact.Role, artifact.Variant, artifact.ChecksumSha256);
        var input = new OperationsDerivativeInputSummary(
            0,
            "source",
            CentralDerivativeInputSourceKind.Artifact,
            true,
            CentralDerivativeInputResolutionState.Resolved,
            null,
            artifactId,
            null,
            new string('D', 64));
        var attempt = new OperationsDerivativeAttemptSummary(
            1,
            "worker",
            CentralDerivativeAttemptOutcome.Completed,
            null,
            now,
            now.AddSeconds(1),
            100,
            50);
        var job = new OperationsCaptureJobTrace(
            jobId,
            FrameArtifactRole.Preview,
            "graph",
            "preview",
            "1",
            CentralDerivativeJobStatus.Completed,
            CentralDerivativeWindowOutcome.Run,
            null,
            now,
            now.AddSeconds(1),
            [input],
            [attempt]);
        var environmentalEvidence = new OperationsEnvironmentalEvidenceSummary(
            Guid.NewGuid(),
            environment.Kind,
            environment.Unit,
            environment.NumericValue,
            environment.BooleanValue,
            environment.Quality,
            environment.ObservedAtUtc,
            "provider",
            "method",
            "1",
            new string('E', 64),
            1);
        var transient = new OperationsTransientEventTrace(
            Guid.NewGuid(),
            1,
            TransientEventState.Provisional,
            TransientClassification.Unknown,
            900_000,
            CentralTransientReviewState.NeedsReview,
            null,
            now,
            1,
            1,
            0);
        var captureDetail = new OperationsCaptureDetail(capture, [artifact], null, role, provenance);
        var trace = new OperationsCaptureTrace([lineage], [job], [environmentalEvidence], [transient], false);
        var jobSummary = new OperationsJobSummary(
            jobId,
            captureId,
            job.TargetRole,
            job.TargetRecipeVersion,
            job.TargetVariant,
            job.Status,
            job.StateReasonCode,
            1,
            3,
            now,
            now,
            now.AddSeconds(1),
            role);

        Assert.AreEqual(observatory, new OperationsObservatoryPage([observatory], null).Items.Single());
        Assert.AreEqual(camera, new OperationsCameraFleetPage([camera], null).Items.Single());
        Assert.AreEqual(installation, new OperationsCameraInstallationPage([installation], null).Items.Single());
        Assert.AreEqual(registration, new OperationsRegistrationPage([registration], null).Items.Single());
        Assert.AreEqual(artifact, new OperationsArtifactPage([artifact], null).Items.Single());
        Assert.AreEqual(capture, new OperationsCapturePage([capture], null).Items.Single());
        Assert.AreEqual(jobSummary, new OperationsJobPage([jobSummary], null).Items.Single());
        Assert.AreEqual(environment, observatoryDetail.Environment.Single());
        Assert.AreEqual(installation, cameraDetail.Installations.Single());
        Assert.AreEqual(provenance, captureDetail.Provenance);
        Assert.AreEqual(job, trace.Jobs.Single());
        Assert.AreEqual(input, trace.Jobs.Single().Inputs.Single());
        Assert.AreEqual(attempt, trace.Jobs.Single().Attempts.Single());
        Assert.AreEqual(environmentalEvidence, trace.EnvironmentalEvidence.Single());
        Assert.AreEqual(transient, trace.Events.Single());
    }
}
