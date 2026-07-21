using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralTransientValidationExecutorTests
{
    [TestMethod]
    public void StateFor_CompleteClassifiedAssessmentWithLimitationNeedsReview()
    {
        var observationId = Guid.NewGuid();
        var assessment = new TransientAssessmentV1(
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            TransientAssessmentAuthority.Authoritative,
            TransientClassification.Meteor,
            TransientMeteorSeverity.Fireball,
            950_000,
            [new TransientReasonV1(
                TransientAssessmentReasonCodes.SaturatedBrightness,
                TransientReasonKind.Limitation,
                [observationId])],
            new TransientAssessmentProducerV1(
                TransientAssessmentProducerV1.CurrentSchemaVersion,
                TransientAssessmentProducerKind.DeterministicAlgorithm,
                "test",
                "v1"),
            new string('A', 64),
            [observationId],
            null);

        CentralTransientValidationExecutor.StateFor(
            TransientCandidateState.Complete, assessment, associationAmbiguous: false)
            .Should().Be(TransientEventState.NeedsReview);
    }

    [TestMethod]
    public void AssociationCompatibility_CoversRoleLayoutLevelsAndAllProfileAxes()
    {
        var layout = new FrameLayoutDescriptor(
            100, 80, 200, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian,
            16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None,
            100, 60_000, 16_000);
        var processing = new ProcessingCompatibilityIdentity(
            "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing");

        CentralTransientValidationExecutor.IsAssociationCompatible(
            FrameArtifactRole.Raw, layout, processing, FrameArtifactRole.Raw, layout, processing).Should().BeTrue();
        CentralTransientValidationExecutor.IsAssociationCompatible(
            FrameArtifactRole.Raw, layout, processing, FrameArtifactRole.Calibrated, layout, processing).Should().BeFalse();
        CentralTransientValidationExecutor.IsAssociationCompatible(
            FrameArtifactRole.Raw, layout, processing, FrameArtifactRole.Raw,
            layout with { BlackLevel = 101 }, processing).Should().BeFalse();
        CentralTransientValidationExecutor.IsAssociationCompatible(
            FrameArtifactRole.Raw, layout, processing, FrameArtifactRole.Raw, layout,
            processing with { Orientation = "other" }).Should().BeFalse();
        CentralTransientValidationExecutor.IsAssociationCompatible(
            FrameArtifactRole.Raw, layout, processing, FrameArtifactRole.Raw, layout,
            processing with { Sensor = "other" }).Should().BeFalse();
    }

    [TestMethod]
    public void RetrospectiveCandidateQuery_FiltersNullDeviceIdentityBeforeBatching()
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=localhost;Database=skymonitor-query;Integrated Security=true;TrustServerCertificate=true")
            .Options);
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            StarMaximumMagnitude = -30
        };
        var recipe = new CentralDerivativeRecipeCatalog(options).GetRequiredRecipes(FrameArtifactRole.Raw)
            .Single(item => item.RecipeName == CentralTransientRuntime.RecipeName);

        var sql = CentralTransientRetrospectiveScheduler.CreateRetrospectiveCandidateQuery(
                context,
                FrameArtifactRole.Raw,
                recipe,
                recipe.Transient!.ExecutionOptionsIdentitySha256)
            .OrderBy(item => item.ReceivedAtUtc)
            .Take(100)
            .ToQueryString();

        sql.Should().Contain("[c].[DevicePublicId] IS NOT NULL");
        sql.IndexOf("[c].[DevicePublicId] IS NOT NULL", StringComparison.Ordinal)
            .Should().BeLessThan(sql.IndexOf("ORDER BY", StringComparison.Ordinal));
    }
}
