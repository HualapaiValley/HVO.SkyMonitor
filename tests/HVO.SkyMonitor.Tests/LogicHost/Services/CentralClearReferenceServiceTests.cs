using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralClearReferenceServiceTests
{
    [TestMethod]
    public void ValidateArtifact_AcceptsReconstructableLinearRawFromSameRig()
    {
        var registrationId = Guid.NewGuid();
        var artifact = CreateArtifact(registrationId, "rig-1", CameraPixelFormat.Mono16);

        var action = () => CentralClearReferenceService.ValidateArtifact(artifact, registrationId, "rig-1");

        action.Should().NotThrow();
    }

    [TestMethod]
    public void ValidateArtifact_RejectsArtifactFromAnotherRig()
    {
        var registrationId = Guid.NewGuid();
        var artifact = CreateArtifact(registrationId, "rig-2", CameraPixelFormat.Mono16);

        var action = () => CentralClearReferenceService.ValidateArtifact(artifact, registrationId, "rig-1");

        action.Should().Throw<CentralClearReferenceException>()
            .Which.ReasonCode.Should().Be("clear-reference.artifact-ineligible");
    }

    [TestMethod]
    public void ValidateArtifact_RejectsUnsupportedOrMalformedEvidence()
    {
        var registrationId = Guid.NewGuid();
        var unsupported = CreateArtifact(registrationId, "rig-1", CameraPixelFormat.Rgb24);
        var malformed = CreateArtifact(registrationId, "rig-1", CameraPixelFormat.Mono16);
        malformed.Frame!.Timing = null;

        var unsupportedAction = () => CentralClearReferenceService.ValidateArtifact(unsupported, registrationId, "rig-1");
        var malformedAction = () => CentralClearReferenceService.ValidateArtifact(malformed, registrationId, "rig-1");

        unsupportedAction.Should().Throw<CentralClearReferenceException>();
        malformedAction.Should().Throw<CentralClearReferenceException>();
    }

    [TestMethod]
    public async Task ControllerMapsInvalidKeysToBadRequest()
    {
        var controller = new ClearReferenceDesignationsController(new InvalidClearReferenceService());

        var get = await controller.GetAsync(Guid.NewGuid(), " ", CancellationToken.None).ConfigureAwait(false);
        var put = await controller.PutAsync(
            Guid.NewGuid(), "rig-1", new SetClearReferenceRequest(Guid.Empty), CancellationToken.None)
            .ConfigureAwait(false);
        var delete = await controller.DeleteAsync(Guid.NewGuid(), " ", CancellationToken.None).ConfigureAwait(false);

        get.Result.Should().BeOfType<BadRequestObjectResult>();
        put.Result.Should().BeOfType<BadRequestObjectResult>();
        delete.Should().BeOfType<BadRequestObjectResult>();
    }

    private static CentralArtifact CreateArtifact(
        Guid registrationId,
        string rigId,
        CameraPixelFormat pixelFormat)
    {
        var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        var frame = new CentralFrame
        {
            RegistrationId = registrationId,
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = Guid.NewGuid(),
            AgentId = "agent-1",
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = now,
            FirstReceivedAtUtc = now,
            RigId = rigId,
            CaptureSequence = 42,
            Timing = new CentralCaptureTiming
            {
                RequestedStartUtc = now,
                ExposureStartedUtc = now.AddMilliseconds(1),
                ExposureEndedUtc = now.AddSeconds(1),
                ReadoutCompletedUtc = now.AddSeconds(2),
                DurableIngressUtc = now.AddSeconds(3)
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
                Sha256 = Hash(kind.ToString())
            });
        }

        var options = CaptureContractJson.SerializeToElement(new { });
        return new CentralArtifact
        {
            CentralFrameId = frame.Id,
            Frame = frame,
            DevicePublicId = frame.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Raw,
            RecipeVersion = "raw-v1",
            ManifestSchemaVersion = ArtifactUploadManifest.CurrentSchemaVersion,
            MediaType = "application/x-hvo-linear-frame",
            ByteLength = 8,
            ChecksumSha256 = Hash("payload"),
            StorageReference = "minio://skymonitor-artifacts/reference",
            ReceivedAtUtc = now,
            IdempotencyKey = Hash("idempotency"),
            SourceId = "camera-capture",
            Variant = "native",
            CreatedUtc = now,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete,
            Layout = new CentralArtifactLayout
            {
                Width = 2,
                Height = 2,
                StrideBytes = pixelFormat == CameraPixelFormat.Rgb24 ? 6 : 4,
                PixelFormat = pixelFormat.ToString(),
                ByteOrder = FrameByteOrder.LittleEndian.ToString(),
                SampleDepthBits = pixelFormat == CameraPixelFormat.Rgb24 ? 8 : 16,
                ContainerDepthBits = pixelFormat == CameraPixelFormat.Rgb24 ? 8 : 16,
                Packing = FrameSamplePacking.ByteAligned.ToString(),
                CfaPattern = ColorFilterArrayPattern.None.ToString(),
                BlackLevel = 0,
                WhiteLevel = pixelFormat == CameraPixelFormat.Rgb24 ? byte.MaxValue : ushort.MaxValue,
                ByteLength = 8
            },
            Recipe = new CentralArtifactRecipe
            {
                Name = "raw-capture",
                SemanticVersion = "1.0.0",
                ImplementationVersion = "test-v1",
                OptionsJson = CaptureContractJson.Canonicalize(options).GetRawText(),
                OptionsSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(options)
            }
        };
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class InvalidClearReferenceService : ICentralClearReferenceService
    {
        public Task<CentralClearReferenceSummary?> GetAsync(
            Guid devicePublicId,
            string rigId,
            CancellationToken cancellationToken) => throw new ArgumentException("invalid");

        public Task<CentralClearReferenceSummary> SetAsync(
            Guid devicePublicId,
            string rigId,
            Guid artifactId,
            string actor,
            CancellationToken cancellationToken) => throw new ArgumentException("invalid");

        public Task<bool> DeleteAsync(
            Guid devicePublicId,
            string rigId,
            CancellationToken cancellationToken) => throw new ArgumentException("invalid");
    }
}
