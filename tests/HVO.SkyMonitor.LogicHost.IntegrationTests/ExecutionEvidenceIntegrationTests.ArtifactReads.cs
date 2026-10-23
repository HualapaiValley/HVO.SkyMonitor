using System.Net;
using System.Net.Http.Json;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ExecutionEvidenceIntegrationTests
{
    [TestMethod]
    public async Task ArtifactReadsJoinExactGoldenReferencesAndKeepAcknowledgementsSeparate()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        var camera = await BindingCameraAsync(device).ConfigureAwait(false);
        using var producer = AssemblyHooks.Fixture.Factory.CreateClient();
        foreach (var name in new[] { "revision-local", "execution-live", "availability" })
        {
            var envelope = Reorigin(await FixtureAsync(name).ConfigureAwait(false), device);
            using var submitted = await PostAsync(producer, device, envelope).ConfigureAwait(false);
            Assert.AreEqual(4, (await FeedbackAsync(submitted).ConfigureAwait(false)).Facts.Length);
        }
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>();
        var units = await service.ListUnitsAsync(device.BindingId, device.OwnerId, device.ObservatoryId).ConfigureAwait(false);
        Assert.IsNotNull(units);
        using var reader = await BindingKeyClientAsync(device).ConfigureAwait(false);
        foreach (var unit in units)
        {
            var canonical = await service.GetUnitAsync(device.BindingId, unit.Id, device.OwnerId, device.ObservatoryId).ConfigureAwait(false);
            var envelope = GraphExecutionEvidenceJson.ParseEnvelope(canonical!).Value!;
            var expected = (envelope.Execution is { } execution
                ? execution.Nodes.SelectMany(n => n.Inputs.Select(i => i.Artifact).Concat(n.Outputs.Select(o => o.Artifact)))
                    .Prepend(new(ExecutionEvidenceArtifactReferenceV1.CurrentSchemaVersion, execution.PrimaryArtifactId, execution.CaptureId))
                : envelope.Availability?.Observations.Select(o => o.Artifact) ?? []).Distinct().ToArray();
            var actual = new List<ExecutionEvidenceArtifactView>();
            string? cursor = null;
            do
            {
                using var response = await reader.GetAsync(new Uri(ArtifactPath(device, camera, unit.Id) + "?take=1" +
                    (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), UriKind.Relative)).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var page = (await response.Content.ReadFromJsonAsync<ExecutionEvidenceUnitArtifactsView>().ConfigureAwait(false))!;
                Assert.AreEqual(ExecutionEvidenceUnitArtifactsView.CurrentSchemaVersion, page.SchemaVersion);
                Assert.AreEqual(unit, page.Association.Unit);
                Assert.AreEqual(envelope.Origin.IdentitySha256, page.Association.OriginIdentitySha256);
                Assert.AreEqual(device.DevicePublicId, page.Association.InstallationPublicId);
                Assert.IsTrue(page.Artifacts.Count <= 1);
                actual.AddRange(page.Artifacts);
                cursor = page.NextCursor;
                Assert.IsTrue(actual.Count <= expected.Length);
            } while (cursor is not null);
            Assert.AreEqual(expected.Length, actual.Count);
            foreach (var reference in actual)
            {
                var exact = expected[reference.Ordinal];
                Assert.AreEqual(exact, ToProducerReference(reference));
                using var response = await reader.GetAsync(new Uri(ObservationPath(device, camera, unit.Id, reference), UriKind.Relative)).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var read = (await response.Content.ReadFromJsonAsync<ExecutionEvidenceArtifactReconciliationView>().ConfigureAwait(false))!;
                Assert.AreEqual(reference, read.Artifact);
                Assert.AreEqual(unit, read.Association.Unit);
                Assert.IsFalse(read.Observations.Any(o => o.State == "Available"), "Acknowledgement and producer availability are not central object availability.");
                Assert.IsTrue(read.Observations.All(o => o.ArtifactReferenceId == reference.ArtifactReferenceId));
            }
        }
    }

    [TestMethod]
    public async Task ArtifactReadsDenyEverySubstitutedIdentityScopeAndRemovedMembership()
    {
        var setup = await PrepareReconciliationAsync().ConfigureAwait(false);
        var device = setup.Device;
        var camera = await BindingCameraAsync(device).ConfigureAwait(false);
        var other = (await PrepareReconciliationAsync().ConfigureAwait(false)).Device;
        var otherCamera = await BindingCameraAsync(other).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>();
        var unit = (await service.ListUnitsAsync(device.BindingId, device.OwnerId, null).ConfigureAwait(false))!.Single(u => u.OriginSequence == 3);
        var mapping = (await service.ListUnitArtifactsAsync(device.ObservatoryId, camera, device.InstallationId,
            device.BindingId, unit.Id, device.OwnerId, null).ConfigureAwait(false))!;
        var reference = mapping.Artifacts.Single();
        var artifacts = ArtifactPath(device, camera, unit.Id);
        var observations = ObservationPath(device, camera, unit.Id, reference);
        using var reader = await BindingKeyClientAsync(device).ConfigureAwait(false);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var member = await db.ObservatoryMemberships.SingleAsync(m => m.ObservatoryId == device.ObservatoryId && m.UserId == device.OwnerId).ConfigureAwait(false);
        var retained = await db.LogicalCameraInstallations.SingleAsync(i => i.Id == device.InstallationId).ConfigureAwait(false);
        retained.RetiredAtUtc = Epoch;
        retained.RetiredByUserId = device.OwnerId;
        retained.RetirementReasonCode = "artifact-read-test";
        (await db.DeviceRegistrations.SingleAsync(r => r.Id == device.RegistrationId).ConfigureAwait(false)).Status = DeviceRegistrationStatus.Revoked;
        foreach (var role in new[] { ObservatoryMembershipRole.Viewer, ObservatoryMembershipRole.Manager, ObservatoryMembershipRole.Owner })
        {
            member.Role = role;
            await db.SaveChangesAsync().ConfigureAwait(false);
            foreach (var path in new[] { artifacts, observations })
            {
                using var allowed = await reader.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode, role.ToString());
            }
        }
        var otherUnit = (await service.ListUnitsAsync(other.BindingId, other.OwnerId, null).ConfigureAwait(false))!.Single(u => u.OriginSequence == 3);
        foreach (var path in new[] { artifacts, observations })
        {
            foreach (var pair in new[]
            {
                (device.ObservatoryId, other.ObservatoryId), (camera, otherCamera),
                (device.InstallationId, other.InstallationId), (device.InstallationId, device.DevicePublicId),
                (device.BindingId, other.BindingId)
            })
            {
                using var denied = await reader.GetAsync(new Uri(path.Replace(pair.Item1.ToString("D"), pair.Item2.ToString("D"), StringComparison.Ordinal), UriKind.Relative)).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
                Assert.IsFalse((await denied.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains("Association", StringComparison.Ordinal));
            }
            using var wrongUnit = await reader.GetAsync(new Uri(path.Replace($"/units/{unit.Id}/", $"/units/{otherUnit.Id}/", StringComparison.Ordinal), UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.NotFound, wrongUnit.StatusCode);
        }
        foreach (var path in new[]
        {
            observations.Replace(reference.CaptureId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal),
            observations.Replace(reference.ArtifactId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal),
            observations.Replace($"/artifacts/{reference.ArtifactReferenceId}/", "/artifacts/9223372036854775807/", StringComparison.Ordinal)
        })
        {
            using var denied = await reader.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
        }
        Assert.IsNull(await service.GetArtifactReconciliationAsync(device.ObservatoryId, camera, device.InstallationId, device.BindingId,
            unit.Id, reference.ArtifactReferenceId, reference.CaptureId, reference.ArtifactId, device.OwnerId, other.ObservatoryId).ConfigureAwait(false));
        using var anonymous = AssemblyHooks.Fixture.Factory.CreateClient();
        using var invalid = AssemblyHooks.Fixture.Factory.CreateClient();
        invalid.DefaultRequestHeaders.Add("X-API-Key", "invalid-artifact-read-key");
        foreach (var caller in new[] { anonymous, invalid })
        {
            foreach (var path in new[] { artifacts, observations })
            {
                using var denied = await caller.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
        }
        await db.ObservatoryMemberships.Where(m => m.ObservatoryId == device.ObservatoryId && m.UserId == device.OwnerId).ExecuteDeleteAsync().ConfigureAwait(false);
        foreach (var path in new[] { artifacts, observations })
        {
            using var denied = await reader.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
        }
    }

    [TestMethod]
    public async Task ArtifactReadsBoundPagesCursorsAndLatestRepeatedObservations()
    {
        var setup = await PrepareReconciliationAsync().ConfigureAwait(false);
        var device = setup.Device;
        var camera = await BindingCameraAsync(device).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>();
        var units = (await service.ListUnitsAsync(device.BindingId, device.OwnerId, null).ConfigureAwait(false))!;
        var execution = units.Single(u => u.OriginSequence == 2);
        var report = units.Single(u => u.OriginSequence == 3);
        var page = (await service.ListUnitArtifactsAsync(device.ObservatoryId, camera, device.InstallationId, device.BindingId,
            execution.Id, device.OwnerId, null, take: 1).ConfigureAwait(false))!;
        Assert.IsNotNull(page.NextCursor);
        foreach (var cursor in new[] { "bad", page.NextCursor + "tampered", new string('X', 4097) })
        {
            Assert.IsNull(await service.ListUnitArtifactsAsync(device.ObservatoryId, camera, device.InstallationId, device.BindingId,
                execution.Id, device.OwnerId, null, cursor).ConfigureAwait(false));
        }
        Assert.IsNull(await service.ListUnitArtifactsAsync(device.ObservatoryId, camera, device.InstallationId, device.BindingId,
            report.Id, device.OwnerId, null, page.NextCursor).ConfigureAwait(false));
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var outsider = await db.Users.Where(u => u.Email == HVO.SkyMonitor.TestSupport.TestUsers.Viewer.Email).Select(u => u.Id).SingleAsync().ConfigureAwait(false);
        db.Add(new ObservatoryMembership { ObservatoryId = device.ObservatoryId, UserId = outsider, Role = ObservatoryMembershipRole.Viewer, AddedAtUtc = Epoch });
        await db.SaveChangesAsync().ConfigureAwait(false);
        Assert.IsNull(await service.ListUnitArtifactsAsync(device.ObservatoryId, camera, device.InstallationId, device.BindingId,
            execution.Id, outsider, null, page.NextCursor).ConfigureAwait(false));
        Assert.IsNull(await service.ListUnitArtifactsAsync(device.ObservatoryId, camera, device.InstallationId, device.BindingId,
            execution.Id, device.OwnerId, device.ObservatoryId, page.NextCursor).ConfigureAwait(false));
        using var reader = await BindingKeyClientAsync(device).ConfigureAwait(false);
        foreach (var take in new[] { 0, -1, 101 })
        {
            using var invalid = await reader.GetAsync(new Uri(ArtifactPath(device, camera, execution.Id) + $"?take={take}", UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        var reference = (await service.ListUnitArtifactsAsync(device.ObservatoryId, camera, device.InstallationId, device.BindingId,
            report.Id, device.OwnerId, null).ConfigureAwait(false))!.Artifacts.Single();
        await ScheduleOnlyReferenceAsync(db, reference.ArtifactReferenceId, DateTimeOffset.UtcNow.AddDays(30)).ConfigureAwait(false);
        var states = new[] { "Missing", "Unresolved", "Quarantined", "Quarantined", "Unresolved", "Available", "Missing" };
        for (var index = 0; index < 57; index++)
        {
            db.Add(new ImportedExecutionEvidenceObservation
            {
                BindingId = device.BindingId,
                ArtifactReferenceId = reference.ArtifactReferenceId,
                State = states[index % states.Length],
                ReasonCode = $"test.reason-{index}",
                ObservedAtUtc = Epoch,
                ObjectGeneration = $"generation-{index}"
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        var read = (await service.GetArtifactReconciliationAsync(device.ObservatoryId, camera, device.InstallationId, device.BindingId,
            report.Id, reference.ArtifactReferenceId, reference.CaptureId, reference.ArtifactId, device.OwnerId, null).ConfigureAwait(false))!;
        Assert.AreEqual(ExecutionEvidenceArtifactReconciliationView.CurrentSchemaVersion, read.SchemaVersion);
        Assert.AreEqual(50, read.Observations.Count);
        Assert.IsTrue(read.ObservationsTruncated);
        Assert.AreEqual("test.reason-56", read.Observations[0].ReasonCode);
        Assert.AreEqual("generation-56", read.Observations[0].ObjectGeneration);
        Assert.IsTrue(read.Observations.Zip(read.Observations.Skip(1)).All(p => p.First.Id > p.Second.Id));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ListUnitArtifactsAsync(device.ObservatoryId, camera,
            device.InstallationId, device.BindingId, report.Id, device.OwnerId, null, cancellationToken: cancellation.Token)).ConfigureAwait(false);
    }

    private static ExecutionEvidenceArtifactReferenceV1 ToProducerReference(ExecutionEvidenceArtifactView a)
        => new(ExecutionEvidenceArtifactReferenceV1.CurrentSchemaVersion, a.ArtifactId, a.CaptureId, a.OutputIdentitySha256,
            a.Role is null ? null : Enum.Parse<HVO.SkyMonitor.AgentCore.FrameArtifactRole>(a.Role), a.Variant, a.PayloadSha256, a.PayloadLength, a.MediaType, a.DescriptorSha256);

    private static string ArtifactPath(Device d, Guid camera, long unit)
        => $"/api/internal/observatories/{d.ObservatoryId:D}/cameras/{camera:D}/installations/{d.InstallationId:D}/execution-evidence/bindings/{d.BindingId:D}/units/{unit}/artifacts";

    private static string ObservationPath(Device d, Guid camera, long unit, ExecutionEvidenceArtifactView a)
        => ArtifactPath(d, camera, unit) + $"/{a.ArtifactReferenceId}/observations?captureId={a.CaptureId:D}&artifactId={a.ArtifactId:D}";
}
