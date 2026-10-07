using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralDerivativeJobInputReaderTests
{
    [TestMethod]
    public async Task LegacyLeaseRetainsLayoutlessSourceIdentityWithoutInlineGeometry()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var inputs = await fixture.Reader.ReadAsync(fixture.Lease with { Inputs = null }, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, inputs.ProcessingInputs);
        var input = inputs.ProcessingInputs.Single();
        Assert.IsNull(input.Descriptor);
        Assert.IsNotNull(input.Artifact);
        Assert.AreEqual(fixture.Artifact.ArtifactId, input.Artifact.ArtifactId);
        Assert.AreEqual(fixture.Evidence.RecipeIdentitySha256, input.Artifact.RecipeIdentitySha256);
        Assert.AreEqual(fixture.Compatibility, input.Artifact.Compatibility);
        Assert.AreEqual(TimeSpan.FromSeconds(3), input.Artifact.Integration);
        CollectionAssert.AreEqual(fixture.Payload, input.Payload.ToArray());
        CollectionAssert.AreEqual(new[] { fixture.SourceId }, input.Artifact.SourceArtifactIds!.ToArray());
        Assert.AreEqual(fixture.Payload.Length, inputs.ByteLength);
        Assert.AreEqual(0, fixture.Jobs.MarkCount);
        // An input with no recorded product facts resolves its kind from its role, exactly as the output writer does,
        // and carries no schema or content identity, so every existing consumer sees the values it saw before.
        Assert.AreEqual(ProcessingProductKind.Metadata, input.Artifact.ProductKind);
        Assert.IsNull(input.Artifact.SchemaVersion);
        Assert.IsNull(input.Artifact.ContentIdentitySha256);
    }

    [TestMethod]
    public async Task LayoutlessInputCarriesDurableProductFactsWithoutMovingAnyIdentity()
    {
        await using var legacy = await Fixture.CreateAsync().ConfigureAwait(false);
        await using var fixture = await Fixture.CreateAsync(
            evidenceKind: ProcessingProductKind.Metadata,
            evidenceSchemaVersion: MeasuredStellarAssociationsV1.CurrentSchemaVersion,
            structuredKind: nameof(ProcessingProductKind.PixelData),
            structuredSchemaVersion: "ignored-schema",
            structuredContentIdentity: new string('C', 64)).ConfigureAwait(false);

        var input = (await fixture.Reader.ReadAsync(fixture.Lease, CancellationToken.None).ConfigureAwait(false))
            .ProcessingInputs.Single();
        var baseline = (await legacy.Reader.ReadAsync(legacy.Lease, CancellationToken.None).ConfigureAwait(false))
            .ProcessingInputs.Single();

        Assert.AreEqual(ProcessingProductKind.Metadata, input.Artifact!.ProductKind, "graph evidence takes precedence");
        Assert.AreEqual(MeasuredStellarAssociationsV1.CurrentSchemaVersion, input.Artifact.SchemaVersion);
        Assert.AreEqual(new string('C', 64), input.Artifact.ContentIdentitySha256);
        // The facts are descriptive only: the bytes, the frozen recipe identity and the compatibility are those of the
        // same input without them.
        CollectionAssert.AreEqual(baseline.Payload.ToArray(), input.Payload.ToArray());
        Assert.AreEqual(baseline.Artifact!.RecipeIdentitySha256, input.Artifact.RecipeIdentitySha256);
        Assert.AreEqual(baseline.Artifact.Compatibility, input.Artifact.Compatibility);
    }

    [TestMethod]
    public async Task LayoutlessInputFallsBackToTheIngestedStructuredProductFacts()
    {
        await using var fixture = await Fixture.CreateAsync(
            structuredKind: nameof(ProcessingProductKind.Metadata),
            structuredSchemaVersion: MeasuredStellarAssociationsV1.CurrentSchemaVersion,
            structuredContentIdentity: new string('D', 64)).ConfigureAwait(false);

        var input = (await fixture.Reader.ReadAsync(fixture.Lease, CancellationToken.None).ConfigureAwait(false))
            .ProcessingInputs.Single();

        Assert.AreEqual(ProcessingProductKind.Metadata, input.Artifact!.ProductKind);
        Assert.AreEqual(MeasuredStellarAssociationsV1.CurrentSchemaVersion, input.Artifact.SchemaVersion);
        Assert.AreEqual(new string('D', 64), input.Artifact.ContentIdentitySha256);
    }

    [TestMethod]
    public async Task LayoutlessInputWithAnInvalidStructuredProductKindIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync(structuredKind: "NotAKind").ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<CentralDerivativeInputRejectedException>(() => fixture.Reader.ReadAsync(
            fixture.Lease, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FrozenSceneBindingIsSeparateFromPayloadInputsAndDescriptionDoesNotLoadPixels()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var reference = new CentralProjectedSceneReference(Guid.NewGuid(), Guid.NewGuid(), fixture.Lease.FrameId,
            new string('B', 64), new string('C', 64),
            new ProjectedSceneSource(fixture.Lease.FrameId, fixture.SourceId, new string('D', 64)), "scene", new string('E', 64));
        var sceneInput = fixture.Lease.Inputs!.Single() with
        {
            Ordinal = 1,
            CentralArtifactId = reference.CentralArtifactId,
            ArtifactId = reference.ArtifactId,
            BindingName = CentralProjectedSceneResolver.BindingName
        };
        var lease = fixture.Lease with { ProjectedScene = reference, Inputs = [sceneInput, fixture.Lease.Inputs!.Single()] };
        var description = await fixture.Reader.DescribeAsync(lease, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, description.References);
        Assert.AreEqual(fixture.Artifact.Id, description.References.Single().CentralArtifactId);
        Assert.IsEmpty(description.ProcessingInputs.Single().Payload.ToArray());
        Assert.AreEqual(fixture.Artifact.ArtifactId, description.ProcessingInputs.Single().Artifact!.ArtifactId);
        Assert.AreEqual(0, fixture.Objects.VerifyCount);
        Assert.AreEqual(0, fixture.Objects.CopyCount);
        var loaded = await fixture.Reader.ReadAsync(lease, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, loaded.ProcessingInputs);
        CollectionAssert.AreEqual(fixture.Payload, loaded.ProcessingInputs.Single().Payload.ToArray());
        Assert.AreEqual(1, fixture.Objects.VerifyCount);
        Assert.AreEqual(1, fixture.Objects.CopyCount);
    }

    [TestMethod]
    public async Task LeaseLostDuringPayloadCopyPreventsDelivery()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        fixture.Objects.AfterCopy = async () =>
        {
            fixture.Job.Status = CentralDerivativeJobStatus.Canceled;
            await fixture.Db.SaveChangesAsync().ConfigureAwait(false);
        };
        await Assert.ThrowsExactlyAsync<CentralDerivativeJobStateException>(() => fixture.Reader.ReadAsync(
            fixture.Lease, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1, fixture.Objects.CopyCount);
        Assert.AreEqual(0, fixture.Jobs.MarkCount, "Lease loss does not misclassify a valid source as corrupt.");
        await Assert.ThrowsExactlyAsync<CentralDerivativeJobStateException>(() => fixture.Reader.DescribeAsync(
            fixture.Lease, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1, fixture.Objects.CopyCount, "A stale lease cannot start another payload read.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedPayloadReadMarksTheExactSourceUnavailableWithoutDeliveringPartialInputs(bool corrupt)
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        fixture.Objects.BeforeFailure = cancellation.Cancel;
        fixture.Objects.CopyFailure = corrupt
            ? new CentralArtifactIntegrityException("object.length-mismatch", "generation")
            : new CentralArtifactMissingException();
        if (corrupt)
            await Assert.ThrowsExactlyAsync<CentralArtifactIntegrityException>(() => fixture.Reader.ReadAsync(
                fixture.Lease, cancellation.Token)).ConfigureAwait(false);
        else
            await Assert.ThrowsExactlyAsync<CentralArtifactMissingException>(() => fixture.Reader.ReadAsync(
                fixture.Lease, cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(1, fixture.Jobs.MarkCount);
        Assert.AreEqual(fixture.Job.Id, fixture.Jobs.JobId);
        Assert.AreEqual(fixture.Lease.LeaseToken, fixture.Jobs.LeaseToken);
        Assert.AreEqual(fixture.Artifact.Id, fixture.Jobs.ArtifactId);
        CollectionAssert.AreEqual(fixture.Artifact.RowVersion, fixture.Jobs.RowVersion!);
        Assert.AreEqual(corrupt, fixture.Jobs.Quarantine);
        Assert.AreEqual(corrupt ? "object.length-mismatch" : "object.missing", fixture.Jobs.ReasonCode);
    }

    [TestMethod]
    [DataRow(-1L)]
    [DataRow(2147483648L)]
    public async Task InvalidStoredInputLengthRejectsDescriptionAndReadBeforeObjectAccess(long byteLength)
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        fixture.Artifact.ByteLength = byteLength;
        await fixture.Db.SaveChangesAsync().ConfigureAwait(false);
        Assert.AreEqual(fixture.Payload.Length, fixture.Lease.Inputs!.Single().ByteLength,
            "A valid-looking lease length must not bypass the stored source bound.");
        await Assert.ThrowsExactlyAsync<CentralDerivativeInputRejectedException>(() => fixture.Reader.DescribeAsync(
            fixture.Lease, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CentralDerivativeInputRejectedException>(() => fixture.Reader.ReadAsync(
            fixture.Lease, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Objects.VerifyCount, "Reject before storage verification or payload allocation.");
        Assert.AreEqual(0, fixture.Objects.CopyCount);
        Assert.AreEqual(0, fixture.Jobs.MarkCount, "An unsupported length must not quarantine or retire the source.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal ApplicationDbContext Db { get; } = new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        internal byte[] Payload { get; } = [1, 2, 3, 4];
        internal Guid SourceId { get; } = Guid.NewGuid();
        internal ProcessingCompatibilityIdentity Compatibility { get; } = new("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile");
        internal CentralArtifact Artifact { get; private set; } = null!;
        internal CentralArtifactProcessingEvidence Evidence { get; private set; } = null!;
        internal CentralDerivativeJob Job { get; private set; } = null!;
        internal CentralDerivativeJobLease Lease { get; private set; } = null!;
        internal ObjectReader Objects { get; private set; } = null!;
        internal JobService Jobs { get; } = new();
        internal CentralDerivativeWorkerTelemetry Telemetry { get; } = new();
        internal CentralDerivativeJobInputReader Reader { get; private set; } = null!;

        internal static async Task<Fixture> CreateAsync(
            ProcessingProductKind? evidenceKind = null,
            string? evidenceSchemaVersion = null,
            string? structuredKind = null,
            string? structuredSchemaVersion = null,
            string? structuredContentIdentity = null)
        {
            var fixture = new Fixture();
            var utc = DateTimeOffset.UtcNow;
            var frame = new CentralFrame
            {
                FrameId = Guid.NewGuid(),
                DevicePublicId = Guid.NewGuid(),
                AgentId = "agent",
                CaptureSequence = 7,
                CapturedAtUtc = utc,
                Timing = new CentralCaptureTiming { ExposureStartedUtc = utc, ExposureEndedUtc = utc.AddSeconds(2) }
            };
            fixture.Artifact = new CentralArtifact
            {
                Frame = frame,
                CentralFrameId = frame.Id,
                DevicePublicId = frame.DevicePublicId,
                ArtifactId = Guid.NewGuid(),
                Role = FrameArtifactRole.Metadata,
                Variant = "frozen-metadata",
                MediaType = "application/octet-stream",
                ByteLength = fixture.Payload.Length,
                ChecksumSha256 = PayloadChecksum.ComputeSha256(fixture.Payload),
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete,
                RowVersion = [9, 8, 7]
            };
            fixture.Artifact.Sources.Add(new CentralArtifactSource { Ordinal = 0, SourceArtifactId = fixture.SourceId });
            fixture.Job = new CentralDerivativeJob
            {
                SourceArtifact = fixture.Artifact,
                SourceCentralArtifactId = fixture.Artifact.Id,
                Status = CentralDerivativeJobStatus.Leased,
                LeaseToken = Guid.NewGuid(),
                LeaseOwner = "worker",
                LeaseExpiresAtUtc = utc.AddMinutes(5)
            };
            fixture.Job.Inputs.Add(new CentralDerivativeJobInput { Ordinal = 0, Artifact = fixture.Artifact, CentralArtifactId = fixture.Artifact.Id });
            fixture.Evidence = new CentralArtifactProcessingEvidence
            {
                Artifact = fixture.Artifact,
                CentralArtifactId = fixture.Artifact.Id,
                Job = fixture.Job,
                CentralDerivativeJobId = fixture.Job.Id,
                RecipeIdentitySha256 = new string('A', 64),
                TotalIntegrationTicks = TimeSpan.FromSeconds(3).Ticks,
                CompatibilityJson = JsonSerializer.Serialize(fixture.Compatibility, JsonSerializerOptions.Web),
                ProductKind = evidenceKind,
                ProductSchemaVersion = evidenceSchemaVersion
            };
            if (structuredKind is not null)
            {
                fixture.Artifact.StructuredProduct = new CentralStructuredProcessingProduct
                {
                    Artifact = fixture.Artifact,
                    CentralArtifactId = fixture.Artifact.Id,
                    OutputIdentitySha256 = new string('E', 64),
                    ProductKind = structuredKind,
                    ProductSchemaVersion = structuredSchemaVersion ?? string.Empty,
                    ContentIdentitySha256 = structuredContentIdentity ?? new string('F', 64)
                };
            }
            fixture.Db.AddRange(fixture.Job, fixture.Evidence);
            await fixture.Db.SaveChangesAsync().ConfigureAwait(false);
            var input = new CentralDerivativeJobLeaseInput(0, fixture.Artifact.Id, frame.DevicePublicId,
                fixture.Artifact.ArtifactId, fixture.Artifact.Role, "recipe-v1", fixture.Artifact.ChecksumSha256,
                fixture.Artifact.MediaType, fixture.Artifact.ByteLength, frame.FrameId, frame.AgentId, frame.CaptureSequence,
                utc, new string('B', 64));
            fixture.Lease = new CentralDerivativeJobLease(fixture.Job.Id, fixture.Job.LeaseToken!.Value, "worker", utc.AddMinutes(5),
                frame.DevicePublicId, fixture.Artifact.ArtifactId, fixture.Artifact.Role, "recipe-v1", "/content",
                fixture.Artifact.ChecksumSha256, fixture.Artifact.MediaType, frame.FrameId, frame.AgentId, utc, null, null,
                FrameArtifactRole.Metadata, "recipe-v1", "output", "fixture", "{}", "{}", new string('A', 64),
                new string('B', 64), null, null, 1, 3, Inputs: [input]);
            fixture.Objects = new ObjectReader(fixture.Payload);
            fixture.Reader = new CentralDerivativeJobInputReader(fixture.Db, fixture.Objects, fixture.Jobs,
                fixture.Telemetry, TimeProvider.System);
            return fixture;
        }

        public ValueTask DisposeAsync()
        {
            Telemetry.Dispose();
            return Db.DisposeAsync();
        }
    }

    private sealed class ObjectReader(byte[] payload) : ICentralArtifactObjectReader
    {
        internal int VerifyCount { get; private set; }
        internal int CopyCount { get; private set; }
        internal Exception? CopyFailure { get; set; }
        internal Action? BeforeFailure { get; set; }
        internal Func<Task>? AfterCopy { get; set; }
        public Task<CentralArtifactObjectSnapshot> VerifyAsync(CentralArtifact artifact, CancellationToken cancellationToken)
        {
            VerifyCount++;
            return Task.FromResult(new CentralArtifactObjectSnapshot("fixture", "generation", payload.Length));
        }
        public Task<bool> IsCurrentGenerationAsync(CentralArtifact artifact, string storageETag, CancellationToken cancellationToken)
            => Task.FromResult(true);
        public async Task CopyToAsync(CentralArtifactObjectSnapshot snapshot, Stream destination, CentralArtifactByteRange? range, CancellationToken cancellationToken)
        {
            CopyCount++;
            if (CopyFailure is not null)
            {
                await destination.WriteAsync(payload.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
                BeforeFailure?.Invoke();
                throw CopyFailure;
            }
            await destination.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            if (AfterCopy is not null) await AfterCopy().ConfigureAwait(false);
        }
    }

    private sealed class JobService : ICentralDerivativeJobService
    {
        internal int MarkCount { get; private set; }
        internal Guid JobId { get; private set; }
        internal Guid LeaseToken { get; private set; }
        internal Guid ArtifactId { get; private set; }
        internal byte[]? RowVersion { get; private set; }
        internal bool Quarantine { get; private set; }
        internal string? ReasonCode { get; private set; }
        public Task MarkInputUnavailableAsync(Guid jobId, Guid leaseToken, Guid centralArtifactId, byte[] expectedSourceRowVersion, string reasonCode, bool quarantine, CancellationToken cancellationToken)
        {
            MarkCount++; JobId = jobId; LeaseToken = leaseToken; ArtifactId = centralArtifactId;
            RowVersion = expectedSourceRowVersion; ReasonCode = reasonCode; Quarantine = quarantine;
            Assert.IsFalse(cancellationToken.CanBeCanceled, "Persist the unavailable source even after an execution cancellation.");
            return Task.CompletedTask;
        }
        public Task<CentralDerivativeJobLease?> ClaimNextAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<CentralDerivativeJobLease> RenewLeaseAsync(Guid jobId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task CompleteAsync(Guid jobId, Guid leaseToken, Guid resultArtifactId, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task CompleteWithoutArtifactAsync(Guid jobId, Guid leaseToken, string reasonCode, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task FailAsync(Guid jobId, Guid leaseToken, string error, bool retryable, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task SkipAsync(Guid jobId, Guid leaseToken, string reasonCode, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
