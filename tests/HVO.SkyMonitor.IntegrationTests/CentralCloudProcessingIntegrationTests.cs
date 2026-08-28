using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CentralCloudProcessingIntegrationTests
{
    [TestMethod]
    public async Task SchedulingSelectsAndPinsCaptureTimePrecipitation()
    {
        var scenario = $"cloud-weather-{Guid.NewGuid():N}";
        await AssemblyHooks.Fixture.SeedActiveDeviceAsync(scenario).ConfigureAwait(false);
        var device = await AssemblyHooks.Fixture.GetActiveDeviceAsync(scenario).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var observationId = Guid.NewGuid();

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var reference = CreateRaw(device, scenario, "rig-1", 1, now.AddMinutes(-10));
        var current = CreateRaw(device, scenario, "rig-1", 2, now);
        var environmentalSource = new EnvironmentalObservationSourceRecord
        {
            IdentitySha256 = Hash("weather-source-identity"),
            ContentSha256 = Hash("weather-source-content"),
            SiteId = device.ObservatoryId,
            AgentId = device.DevicePublicId,
            RigId = "rig-1",
            Provider = "integration",
            SourceId = scenario,
            Version = "1",
            Kind = EnvironmentalObservationSourceKind.Measured,
            MethodName = "rain-gauge",
            MethodVersion = "1",
            ParametersJson = "{}",
            ParametersSha256 = Hash("{}"),
            CreatedAtUtc = now.AddMinutes(-5)
        };
        var observation = new EnvironmentalObservationRecord
        {
            SourceRecordId = environmentalSource.Id,
            Source = environmentalSource,
            SiteId = device.ObservatoryId,
            AgentId = device.DevicePublicId,
            RigId = "rig-1",
            SourceKind = EnvironmentalObservationSourceKind.Measured,
            SourceIdentitySha256 = environmentalSource.IdentitySha256,
            ObservationId = observationId,
            SchemaVersion = EnvironmentalObservationV1.CurrentSchemaVersion,
            Kind = EnvironmentalObservationKind.RainState,
            Unit = EnvironmentalObservationUnit.Boolean,
            BooleanValue = true,
            Quality = EnvironmentalObservationQuality.Good,
            ObservedAtUtc = now.AddSeconds(-2),
            ObservedFromUtc = now.AddMinutes(-1),
            ObservedThroughUtc = now.AddMinutes(1),
            ValidFromUtc = now.AddMinutes(-1),
            ValidThroughUtc = now.AddMinutes(10),
            StaleAfterUtc = now.AddMinutes(5),
            ReceivedAtUtc = now.AddSeconds(-1),
            ClockDiagnostic = EnvironmentalClockDiagnostic.WithinTolerance,
            PayloadSha256 = Hash("rain-observation")
        };
        db.CentralArtifacts.AddRange(reference, current);
        db.CentralClearReferenceDesignations.Add(new CentralClearReferenceDesignation
        {
            RegistrationId = device.RegistrationId,
            RigId = "rig-1",
            CentralArtifactId = reference.Id,
            Artifact = reference,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            UpdatedBy = "integration-test"
        });
        db.EnvironmentalObservationSources.Add(environmentalSource);
        db.EnvironmentalObservations.Add(observation);
        await db.SaveChangesAsync().ConfigureAwait(false);

        await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
            .EnsureRequiredJobsAsync(current, now, CancellationToken.None).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var canonical = await db.CentralDerivativeJobCanonicalInputs
            .Include(input => input.Job)
            .SingleAsync(input => input.Job!.SourceCentralArtifactId == current.Id
                && input.Job.RecipeName == BuiltInProcessingRecipes.CloudAssessment).ConfigureAwait(false);

        canonical.EnvironmentalObservationRecordId.Should().Be(observation.Id);
        canonical.CanonicalJson.Should().Contain("\"precipitationStatus\":\"Fresh\"");
        canonical.CanonicalJson.Should().Contain($"\"precipitationObservationId\":\"{observationId:D}\"");
        canonical.CanonicalJson.Should().Contain($"\"precipitationContentSha256\":\"{observation.PayloadSha256}\"");
        canonical.CanonicalJson.Should().Contain("\"precipitationDetected\":true");
    }

    [TestMethod]
    public async Task DesignationFreezesReferenceEnvironmentAndExpectedIdentityIntoLease()
    {
        var scenario = $"cloud-{Guid.NewGuid():N}";
        await AssemblyHooks.Fixture.SeedActiveDeviceAsync(scenario).ConfigureAwait(false);
        var device = await AssemblyHooks.Fixture.GetActiveDeviceAsync(scenario).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var reference = CreateRaw(device, scenario, "rig-1", 1, now.AddMinutes(-10));
        var replacement = CreateRaw(device, scenario, "rig-1", 2, now.AddMinutes(-5));
        var current = CreateRaw(device, scenario, "rig-1", 3, now);
        db.CentralArtifacts.AddRange(reference, replacement, current);
        db.CentralClearReferenceDesignations.Add(new CentralClearReferenceDesignation
        {
            RegistrationId = device.RegistrationId,
            RigId = "rig-1",
            CentralArtifactId = reference.Id,
            Artifact = reference,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            UpdatedBy = "integration-test"
        });
        await db.SaveChangesAsync().ConfigureAwait(false);

        await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
            .EnsureRequiredJobsAsync(current, now, CancellationToken.None).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var cloud = await db.CentralDerivativeJobs
            .Include(job => job.InputRequirements)
            .Include(job => job.Inputs)
            .Include(job => job.CanonicalInputs)
            .SingleAsync(job => job.SourceCentralArtifactId == current.Id
                && job.RecipeName == BuiltInProcessingRecipes.CloudAssessment).ConfigureAwait(false);

        cloud.InputRequirements.Should().HaveCount(3);
        cloud.Inputs.OrderBy(input => input.Ordinal).Select(input => input.CentralArtifactId)
            .Should().Equal(current.Id, reference.Id);
        cloud.InputRequirements.Single(requirement => requirement.BindingName == "clear-reference")
            .ExpectedCentralArtifactId.Should().Be(reference.Id);
        cloud.ExpectedRecipeIdentitySha256.Should().NotBe(cloud.RequestedRecipeIdentitySha256);
        var canonical = cloud.CanonicalInputs.Should().ContainSingle().Subject;
        canonical.SchemaVersion.Should().Be(CloudAssessmentEnvironmentV1.CurrentSchemaVersion);
        canonical.IdentitySha256.Should().Be(
            ProcessingIdentity.ComputePayloadSha256(Encoding.UTF8.GetBytes(canonical.CanonicalJson)));
        canonical.EnvironmentalObservationRecordId.Should().BeNull();
        canonical.CanonicalJson.Should().Contain("\"precipitationStatus\":\"Missing\"");

        await db.CentralClearReferenceDesignations
            .Where(designation => designation.RegistrationId == device.RegistrationId && designation.RigId == "rig-1")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(designation => designation.CentralArtifactId, replacement.Id)
                .SetProperty(designation => designation.UpdatedAtUtc, now.AddMinutes(1))).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
            .EnsureRequiredJobsAsync(current, now.AddMinutes(1), CancellationToken.None).ConfigureAwait(false);
        db.ChangeTracker.Clear();

        var frozenReferenceId = await db.CentralDerivativeJobInputRequirements
            .Where(requirement => requirement.CentralDerivativeJobId == cloud.Id
                && requirement.BindingName == "clear-reference")
            .Select(requirement => requirement.ExpectedCentralArtifactId)
            .SingleAsync().ConfigureAwait(false);
        frozenReferenceId.Should().Be(reference.Id);
        (await db.CentralDerivativeJobs.CountAsync(job => job.SourceCentralArtifactId == current.Id
            && job.RecipeName == BuiltInProcessingRecipes.CloudAssessment).ConfigureAwait(false)).Should().Be(1);

        await db.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId == current.Id
                && job.Id != cloud.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)).ConfigureAwait(false);
        await db.CentralDerivativeJobs.Where(job => job.Id == cloud.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.CreatedAtUtc, DateTimeOffset.UnixEpoch)
                .SetProperty(job => job.AvailableAtUtc, DateTimeOffset.UnixEpoch)).ConfigureAwait(false);
        var lease = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
            .ClaimNextAsync(scenario, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);

        lease.Should().NotBeNull();
        lease!.JobId.Should().Be(cloud.Id);
        lease.ExpectedRecipeIdentitySha256.Should().Be(cloud.ExpectedRecipeIdentitySha256);
        lease.Inputs.Should().HaveCount(2);
        lease.CanonicalInputs.Should().ContainSingle(input => input.BindingName == "environment");

        var preview = CreateDerived(
            current,
            FrameArtifactRole.Preview,
            CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
            CentralDerivativeRecipeCatalog.PreviewVariant,
            "image/jpeg",
            byteLength: 4,
            includeLayout: true);
        var assessment = CreateDerived(
            current,
            FrameArtifactRole.Metadata,
            CentralDerivativeRecipeCatalog.CloudAssessmentRecipeVersion,
            CentralDerivativeRecipeCatalog.CloudAssessmentVariant,
            "application/vnd.hvo.cloud-assessment+json",
            byteLength: canonical.ByteLength,
            includeLayout: false);
        assessment.Sources.Add(new CentralArtifactSource
        {
            Ordinal = 0,
            SourceArtifactId = current.ArtifactId,
            ResolvedCentralArtifactId = current.Id
        });
        assessment.Sources.Add(new CentralArtifactSource
        {
            Ordinal = 1,
            SourceArtifactId = reference.ArtifactId,
            ResolvedCentralArtifactId = reference.Id
        });
        db.CentralArtifacts.AddRange(preview, assessment);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.CentralArtifactProcessingEvidence.Add(new CentralArtifactProcessingEvidence
        {
            CentralArtifactId = assessment.Id,
            DevicePublicId = device.DevicePublicId,
            Artifact = assessment,
            OutputIdentitySha256 = Hash("assessment-output"),
            RequestedRecipeIdentitySha256 = cloud.RequestedRecipeIdentitySha256,
            RecipeIdentitySha256 = cloud.ExpectedRecipeIdentitySha256,
            AlgorithmsJson = "[]",
            CompatibilityJson = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(
                new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing"))).GetRawText(),
            TotalIntegrationTicks = TimeSpan.FromSeconds(1).Ticks,
            CentralDerivativeJobId = cloud.Id,
            AttemptNumber = 1,
            CreatedAtUtc = now
        });
        await db.CentralDerivativeJobs.Where(job => job.Id == cloud.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.Completed)
                .SetProperty(job => job.ResultCentralArtifactId, assessment.Id)
                .SetProperty(job => job.CompletedAtUtc, now)
                .SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null)).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ChangeTracker.Clear();

        (await db.CentralArtifacts.CountAsync(artifact => artifact.CentralFrameId == current.CentralFrameId
            && artifact.Role == FrameArtifactRole.Preview).ConfigureAwait(false)).Should().Be(1);
        (await db.CentralDerivativeJobs.CountAsync(job => job.Id == cloud.Id
            && job.Status == CentralDerivativeJobStatus.Completed
            && job.ResultCentralArtifactId == assessment.Id
            && job.CanonicalInputs.Any()).ConfigureAwait(false)).Should().Be(1);
        (await db.CentralArtifactProcessingEvidence.AnyAsync(evidence =>
            evidence.CentralArtifactId == assessment.Id).ConfigureAwait(false)).Should().BeTrue();

        await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
            .EnsureRequiredJobsAsync(device.DevicePublicId, assessment.ArtifactId, now, CancellationToken.None)
            .ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var overlay = await db.CentralDerivativeJobs
            .Include(job => job.InputRequirements)
            .Include(job => job.Inputs)
            .Include(job => job.CanonicalInputs)
            .SingleAsync(job => job.RecipeName == BuiltInProcessingRecipes.WeatherCloudOverlay
                && job.SourceCentralArtifactId == preview.Id).ConfigureAwait(false);
        overlay.Inputs.OrderBy(input => input.Ordinal).Select(input => input.CentralArtifactId)
            .Should().Equal(preview.Id, assessment.Id);
        overlay.InputRequirements.Single(input => input.ExpectedCentralArtifactId == assessment.Id)
            .BindingName.Should().Be("assessment");
        overlay.CanonicalInputs.Should().ContainSingle(input =>
            input.IdentitySha256 == canonical.IdentitySha256 && input.CanonicalJson == canonical.CanonicalJson);
        overlay.ExpectedRecipeIdentitySha256.Should().NotBe(overlay.RequestedRecipeIdentitySha256);
    }

    private static CentralArtifact CreateRaw(
        ActiveDeviceFixture device,
        string agentId,
        string rigId,
        long sequence,
        DateTimeOffset capturedAtUtc)
    {
        var frame = new CentralFrame
        {
            RegistrationId = device.RegistrationId,
            DevicePublicId = device.DevicePublicId,
            ObservatoryId = device.ObservatoryId,
            AgentId = agentId,
            FrameId = Guid.NewGuid(),
            RigId = rigId,
            CaptureSequence = sequence,
            CapturedAtUtc = capturedAtUtc,
            FirstReceivedAtUtc = capturedAtUtc,
            Timing = new CentralCaptureTiming
            {
                RequestedStartUtc = capturedAtUtc.AddSeconds(-2),
                ExposureStartedUtc = capturedAtUtc.AddSeconds(-1),
                ExposureEndedUtc = capturedAtUtc,
                ReadoutCompletedUtc = capturedAtUtc.AddMilliseconds(10),
                DurableIngressUtc = capturedAtUtc.AddMilliseconds(20)
            },
            Control = new CentralCaptureControl
            {
                RequestedExposureTicks = TimeSpan.FromSeconds(1).Ticks,
                EffectiveExposureTicks = TimeSpan.FromSeconds(1).Ticks,
                RequestedGain = 1,
                EffectiveGain = 1
            }
        };
        foreach (var kind in Enum.GetValues<CentralProfileKind>())
        {
            frame.Profiles.Add(new CentralCaptureProfile
            {
                Kind = kind,
                Name = kind.ToString(),
                Version = "1",
                Sha256 = Hash($"{agentId}-{kind}")
            });
        }
        var options = CaptureContractJson.SerializeToElement(new { });
        return new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            DevicePublicId = device.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Raw,
            RecipeVersion = "raw-v1",
            ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
            MediaType = "application/x-hvo-linear-frame",
            ByteLength = 8,
            ChecksumSha256 = Hash($"payload-{sequence}"),
            StorageReference = $"minio://skymonitor-artifacts/{agentId}/{sequence}.raw",
            ReceivedAtUtc = capturedAtUtc,
            IdempotencyKey = Hash($"{agentId}-idempotency-{sequence}"),
            SourceId = "integration-camera",
            Variant = "native",
            CreatedUtc = capturedAtUtc,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete,
            Layout = new CentralArtifactLayout
            {
                Width = 2,
                Height = 2,
                StrideBytes = 4,
                PixelFormat = CameraPixelFormat.Mono16.ToString(),
                ByteOrder = FrameByteOrder.LittleEndian.ToString(),
                SampleDepthBits = 16,
                ContainerDepthBits = 16,
                Packing = FrameSamplePacking.ByteAligned.ToString(),
                CfaPattern = ColorFilterArrayPattern.None.ToString(),
                BlackLevel = 0,
                WhiteLevel = ushort.MaxValue,
                ByteLength = 8
            },
            Recipe = new CentralArtifactRecipe
            {
                Name = "raw-capture",
                SemanticVersion = "1.0.0",
                ImplementationVersion = "integration-v1",
                OptionsJson = CaptureContractJson.Canonicalize(options).GetRawText(),
                OptionsSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(options)
            }
        };
    }

    private static CentralArtifact CreateDerived(
        CentralArtifact source,
        FrameArtifactRole role,
        string recipeVersion,
        string variant,
        string mediaType,
        long byteLength,
        bool includeLayout)
    {
        var artifact = new CentralArtifact
        {
            CentralFrameId = source.CentralFrameId,
            DevicePublicId = source.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = role,
            RecipeVersion = recipeVersion,
            ManifestSchemaVersion = "central-v1",
            MediaType = mediaType,
            ByteLength = byteLength,
            ChecksumSha256 = Hash($"{role}-{variant}"),
            StorageReference = $"minio://skymonitor-artifacts/derived/{Guid.NewGuid():N}",
            ReceivedAtUtc = source.ReceivedAtUtc,
            IdempotencyKey = Hash($"{role}-{variant}-idempotency"),
            SourceId = "central-derivative-worker",
            Variant = variant,
            CreatedUtc = source.CreatedUtc,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete,
            Recipe = new CentralArtifactRecipe
            {
                Name = role == FrameArtifactRole.Preview
                    ? BuiltInProcessingRecipes.EncodedPreview
                    : BuiltInProcessingRecipes.CloudAssessment,
                SemanticVersion = "1.0.0",
                ImplementationVersion = "integration-v1",
                OptionsJson = "{}",
                OptionsSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(
                    CaptureContractJson.SerializeToElement(new { }))
            }
        };
        if (includeLayout)
        {
            artifact.Layout = new CentralArtifactLayout
            {
                Width = 2,
                Height = 2,
                StrideBytes = 2,
                PixelFormat = CameraPixelFormat.Mono8.ToString(),
                ByteOrder = FrameByteOrder.LittleEndian.ToString(),
                SampleDepthBits = 8,
                ContainerDepthBits = 8,
                Packing = FrameSamplePacking.ByteAligned.ToString(),
                CfaPattern = ColorFilterArrayPattern.None.ToString(),
                BlackLevel = 0,
                WhiteLevel = byte.MaxValue,
                ByteLength = byteLength
            };
        }
        return artifact;
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
