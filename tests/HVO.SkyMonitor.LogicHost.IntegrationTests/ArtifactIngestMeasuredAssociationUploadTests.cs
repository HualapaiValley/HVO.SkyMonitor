using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ArtifactIngestTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task MultipartIngest_MeasuredAssociationsAreDurableAndDuplicateDeliveryIsIdempotent()
    {
        var data = await CreateMeasuredAssociationCaseAsync().ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        try
        {
            var product = await CreateEdgeMeasuredAssociationProductAsync(data).ConfigureAwait(false);
            var manifest = CreateMeasuredAssociationManifest(data.Raw, product);
            using var client = AssemblyHooks.Fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                await GetSystemTokenAsync(client).ConfigureAwait(false));
            using (var upload = await PostAsync(client, manifest, product.Payload.ToArray()).ConfigureAwait(false))
                upload.StatusCode.Should().Be(HttpStatusCode.Accepted,
                    await upload.Content.ReadAsStringAsync().ConfigureAwait(false));
            using (var duplicate = await PostAsync(client, manifest, product.Payload.ToArray()).ConfigureAwait(false))
                duplicate.StatusCode.Should().Be(HttpStatusCode.Accepted,
                    await duplicate.Content.ReadAsStringAsync().ConfigureAwait(false));

            var stored = await db.CentralArtifacts.AsNoTracking().Include(item => item.StructuredProduct)
                .Include(item => item.Sources).SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId)
                .ConfigureAwait(false);
            stored.ObjectState.Should().Be(CentralArtifactObjectState.Available);
            stored.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
            stored.StructuredProduct!.ContentIdentitySha256.Should().Be(product.ContentIdentitySha256);
            stored.Sources.Should().ContainSingle().Which.SourceArtifactId.Should().Be(data.Raw.Descriptor.Artifact.ArtifactId);
            (await db.CentralArtifacts.CountAsync(item => item.IdempotencyKey == manifest.IdempotencyKey)
                .ConfigureAwait(false)).Should().Be(1);
            using var owner = await ArtifactRetrievalTests.CreateUserClientAsync(
                TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
            var retrieved = await ReadDerivativeAsync(owner, stored.DevicePublicId, stored.ArtifactId).ConfigureAwait(false);
            retrieved.Should().Equal(product.Payload.ToArray());
            MeasuredStellarAssociationJson.Parse(retrieved).Associations!.AssociationIdentitySha256
                .Should().Be(product.ContentIdentitySha256);
        }
        finally
        {
            await TerminalizeCaptureAsync(db, data.Raw.Descriptor.Capture.CaptureId).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("identity", "Structured product schema or content identity does not match its manifest.")]
    [DataRow("capture", "Measured association capture lineage does not match its manifest.")]
    [DataRow("descriptor", "Measured association source descriptor identity does not match its manifest.")]
    [DataRow("sources", "Structured product payload lineage does not match its manifest.")]
    public async Task MultipartIngest_MeasuredAssociationsRejectTamperedIdentityAndLineage(string tamper, string expectedError)
    {
        var data = await CreateMeasuredAssociationCaseAsync().ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        try
        {
            var product = await CreateEdgeMeasuredAssociationProductAsync(data).ConfigureAwait(false);
            var associations = MeasuredStellarAssociationJson.Parse(product.Payload).Associations!;
            associations = associations with
            {
                Source = tamper switch
                {
                    "capture" => associations.Source with { CaptureId = Guid.NewGuid() },
                    "descriptor" => associations.Source with { DescriptorIdentitySha256 = new string('A', 64) },
                    "sources" => associations.Source with { ArtifactId = Guid.NewGuid() },
                    _ => associations.Source
                }
            };
            var payload = MeasuredStellarAssociationJson.Serialize(associations);
            product = product with
            {
                Payload = payload,
                ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(payload),
                ContentIdentitySha256 = tamper == "identity" ? new string('A', 64) : associations.AssociationIdentitySha256
            };
            var manifest = CreateMeasuredAssociationManifest(data.Raw, product);
            using var client = AssemblyHooks.Fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                await GetSystemTokenAsync(client).ConfigureAwait(false));
            using var response = await PostAsync(client, manifest, payload).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Should().Contain(expectedError);
            (await db.CentralArtifacts.CountAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId)
                .ConfigureAwait(false)).Should().Be(0);
        }
        finally
        {
            await TerminalizeCaptureAsync(db, data.Raw.Descriptor.Capture.CaptureId).ConfigureAwait(false);
        }
    }

    private static async Task<ProcessingProduct> CreateEdgeMeasuredAssociationProductAsync(MeasuredAssociationCase data)
    {
        var scene = ProjectedSceneJson.Parse(data.SceneBytes).Scene!;
        var input = new ProcessingAuxiliaryInput(BuiltInProcessingRecipes.MeasuredStellarAssociationsSceneInputName,
            ProcessingAuxiliaryInputKind.CanonicalJson, SchemaVersion: scene.SchemaVersion,
            IdentitySha256: scene.SceneIdentitySha256, Payload: data.SceneBytes)
        {
            ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(data.SceneBytes)
        };
        var source = LogicHostRecipeExecutionAdapter.CreateArtifact(data.Raw.Descriptor) with { Payload = data.Pixels };
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.MeasuredStellarAssociations, JsonSerializer.SerializeToElement(new { }),
            ProcessingInputSelector.Raw(source.Variant), [source], "measured-stellar-associations-v1",
            AuxiliaryInputs: [input], InputArtifactId: source.ArtifactId), CancellationToken.None).ConfigureAwait(false);
        outcome.Status.Should().Be(ProcessingOutcomeStatus.Produced, outcome.ReasonCode);
        return outcome.Products.Should().ContainSingle().Which;
    }

    private static StructuredProcessingProductManifestV1 CreateMeasuredAssociationManifest(
        ArtifactManifestV2 raw, ProcessingProduct product)
    {
        var artifact = new ArtifactDescriptor(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
            product.Role, "measured-associations-step", product.Variant, raw.Descriptor.Timing.ReadoutCompletedUtc,
            product.SourceArtifactIds, product.Recipe.Descriptor, product.MediaType, product.ChecksumSha256);
        return new(StructuredProcessingProductManifestV1.CurrentSchemaVersion,
            new(raw.Descriptor, artifact, product.OutputIdentitySha256, product.Algorithms, product.Compatibility,
                product.TotalIntegration.Ticks, product.Payload.Length, product.Kind, product.SchemaVersion!,
                product.ContentIdentitySha256!), "derived/measured-associations.json", "measured-associations-step");
    }
}
