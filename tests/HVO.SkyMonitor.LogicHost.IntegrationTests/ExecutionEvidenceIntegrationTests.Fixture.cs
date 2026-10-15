using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ExecutionEvidenceIntegrationTests
{
    private static async Task<Device> SeedAsync(bool golden = false, bool createBinding = true)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var origin = (await FixtureAsync("revision-local").ConfigureAwait(false)).Origin;
        var assignment = (await FixtureAsync("revision-assigned").ConfigureAwait(false)).GraphRevision!.Assignment!;
        if (!golden)
        {
            origin = GraphExecutionEvidenceJson.BindIdentity(origin with
            {
                OriginInstallationId = Guid.NewGuid(),
                AgentInstanceId = Guid.NewGuid(),
                BootSessionId = Guid.NewGuid(),
                ObservatoryId = null,
                LogicalCameraInstallationId = null,
                InstallationPublicId = null
            });
        }
        var observatoryId = golden ? origin.ObservatoryId!.Value : Guid.NewGuid();
        var installationId = golden ? origin.LogicalCameraInstallationId!.Value : Guid.NewGuid();
        var publicId = golden ? origin.InstallationPublicId!.Value : Guid.NewGuid();
        var registrationId = golden ? assignment.RegistrationId : Guid.NewGuid();
        var site = new Observatory
        {
            Id = observatoryId,
            OwnerUserId = owner.Id,
            Name = $"Evidence {observatoryId:N}",
            TimeZoneId = "UTC",
            CreatedAtUtc = Epoch
        };
        db.Observatories.Add(site);
        db.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            ObservatoryId = site.Id,
            UserId = owner.Id,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = Epoch
        });
        var deviceId = $"evidence-{registrationId:N}";
        var key = $"key-{Guid.NewGuid():N}";
        db.DeviceRegistrations.Add(new DeviceRegistration
        {
            Id = registrationId,
            ObservatoryId = site.Id,
            DeviceId = deviceId,
            DevicePublicId = publicId,
            DeviceKeyHash = DeviceRegistrationService.ComputeSha256(key),
            Status = DeviceRegistrationStatus.Active,
            OwnerUserId = owner.Id,
            OwnerDisplayName = "Evidence owner",
            FriendlyName = "Evidence camera",
            ObservatoryName = site.Name,
            VerificationCodeHash = new string('A', 64),
            IssuedAtUtc = Epoch,
            ActivatedAtUtc = Epoch,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1)
        });
        var camera = new LogicalCamera
        {
            ObservatoryId = site.Id,
            Name = "Evidence camera",
            Slug = $"evidence-{registrationId:N}",
            CreatedAtUtc = Epoch,
            CreatedByUserId = owner.Id
        };
        db.LogicalCameras.Add(camera);
        db.LogicalCameraInstallations.Add(new LogicalCameraInstallation
        {
            Id = installationId,
            LogicalCameraId = camera.Id,
            RegistrationId = registrationId,
            InstallationPublicId = publicId,
            AssignedAtUtc = Epoch.AddDays(-1),
            AssignedByUserId = owner.Id,
            AssignmentReasonCode = "integration-evidence"
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        var bindingId = Guid.Empty;
        if (createBinding)
        {
            var binding = await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>().CreateBindingAsync(
                new(site.Id, registrationId, installationId, origin.OriginInstallationId, origin.AgentInstanceId), owner.Id, null).ConfigureAwait(false);
            Assert.IsNotNull(binding);
            bindingId = binding.Id;
        }
        return new(deviceId, key, registrationId, site.Id, installationId, publicId, bindingId, owner.Id, origin);
    }

    private static async Task SeedGoldenAssignmentAsync(Device device)
    {
        var revision = (await FixtureAsync("revision-assigned").ConfigureAwait(false)).GraphRevision!;
        await SeedAssignmentAsync(device, revision).ConfigureAwait(false);
    }

    private static async Task SeedAssignmentAsync(Device device, GraphRevisionEvidenceV1 revision)
    {
        var assignment = revision.Assignment!;
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.CentralProcessingGraphRevisions.Add(new CentralProcessingGraphRevision
        {
            Id = assignment.CatalogRevisionId,
            Name = $"Evidence fixture {assignment.CatalogRevisionId:N}",
            Revision = "fixture-1",
            DefinitionJson = revision.CanonicalDefinition.GetRawText(),
            DefinitionIdentitySha256 = revision.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = revision.SharedPlanIdentitySha256,
            EdgePlanIdentitySha256 = revision.SharedPlanIdentitySha256,
            CreatedAtUtc = Epoch,
            CreatedByUserId = device.OwnerId
        });
        db.CentralProcessingGraphAssignments.Add(new CentralProcessingGraphAssignment
        {
            Id = assignment.AssignmentId,
            RevisionId = assignment.CatalogRevisionId,
            TargetHost = CentralProcessingGraphTargetHost.Edge,
            Scope = CentralProcessingGraphAssignmentScope.Observatory,
            ObservatoryId = device.ObservatoryId,
            EffectiveFromUtc = Epoch,
            CreatedAtUtc = Epoch,
            ActorUserId = device.OwnerId,
            ReasonCode = "fixture"
        });
        db.CentralProcessingGraphDeliveryProposals.Add(new CentralProcessingGraphDeliveryProposal
        {
            Id = assignment.ProposalId,
            AssignmentId = assignment.AssignmentId,
            RevisionId = assignment.CatalogRevisionId,
            RegistrationId = assignment.RegistrationId,
            LogicalCameraInstallationId = assignment.LogicalCameraInstallationId,
            InstallationPublicId = assignment.InstallationPublicId,
            CapabilitySnapshotSha256 = assignment.CapabilitySnapshotSha256,
            IssuedAtUtc = assignment.IssuedAtUtc,
            ExpiresAtUtc = assignment.IssuedAtUtc.AddHours(1)
        });
        db.CentralProcessingGraphDeliveryFacts.Add(new CentralProcessingGraphDeliveryFact
        {
            ProposalId = assignment.ProposalId,
            Kind = "Accepted",
            Source = "CameraAgent",
            OccurredAtUtc = assignment.AcceptedAtUtc,
            RecordedAtUtc = assignment.AcceptedAtUtc,
            LocalRevisionId = revision.RevisionId,
            DefinitionIdentitySha256 = revision.DefinitionIdentitySha256,
            SharedPlanIdentitySha256 = revision.SharedPlanIdentitySha256,
            LocalPlanIdentitySha256 = revision.LocalPlanIdentitySha256
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task AssertAssignmentProposalNondisclosureAsync(HttpClient client, Device device)
    {
        var fixture = await FixtureAsync("revision-assigned").ConfigureAwait(false);
        ExecutionEvidenceEnvelopeV1 Assigned(Device authority)
        {
            var revision = fixture.GraphRevision!;
            var assignment = revision.Assignment! with
            {
                ProposalId = Guid.NewGuid(),
                AssignmentId = Guid.NewGuid(),
                CatalogRevisionId = Guid.NewGuid(),
                RegistrationId = authority.RegistrationId,
                LogicalCameraInstallationId = authority.InstallationId,
                InstallationPublicId = authority.DevicePublicId
            };
            return GraphExecutionEvidenceJson.Seal(fixture with
            {
                EvidenceId = Guid.NewGuid(),
                OriginSequence = 1,
                Origin = GraphExecutionEvidenceJson.BindIdentity(authority.Origin with { BootSessionId = Guid.NewGuid() }),
                GraphRevision = revision with { Assignment = assignment }
            });
        }

        var foreignObservatory = await SeedAsync(createBinding: false).ConfigureAwait(false);
        var wrongInstallation = await SeedAsync(createBinding: false).ConfigureAwait(false);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            // A second valid registration/installation at the same observatory is still outside this binding.
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.DeviceRegistrations.SingleAsync(r => r.Id == wrongInstallation.RegistrationId).ConfigureAwait(false);
            var installation = await db.LogicalCameraInstallations.Include(i => i.LogicalCamera)
                .SingleAsync(i => i.Id == wrongInstallation.InstallationId).ConfigureAwait(false);
            registration.ObservatoryId = device.ObservatoryId;
            installation.LogicalCamera!.ObservatoryId = device.ObservatoryId;
            await db.SaveChangesAsync().ConfigureAwait(false);
            wrongInstallation = wrongInstallation with { ObservatoryId = device.ObservatoryId };
        }
        var foreign = Assigned(foreignObservatory);
        var wrong = Assigned(wrongInstallation);
        await SeedAssignmentAsync(foreignObservatory, foreign.GraphRevision!).ConfigureAwait(false);
        await SeedAssignmentAsync(wrongInstallation, wrong.GraphRevision!).ConfigureAwait(false);
        var missing = Assigned(device);
        var assignment = missing.GraphRevision!.Assignment!;
        foreach (var proposalId in new[] { assignment.ProposalId, foreign.GraphRevision!.Assignment!.ProposalId,
            wrong.GraphRevision!.Assignment!.ProposalId })
        {
            // Every field except the untrusted proposal ID is identical, including origin/sequence/evidence ID.
            var probe = GraphExecutionEvidenceJson.Seal(missing with
            {
                GraphRevision = missing.GraphRevision with { Assignment = assignment with { ProposalId = proposalId } }
            });
            using var response = await PostAsync(client, device, probe).ConfigureAwait(false);
            var feedback = await FeedbackAsync(response).ConfigureAwait(false);
            Assert.IsEmpty(feedback.Facts, "Foreign and absent proposal IDs must expose the same retryable response.");
            Assert.AreEqual(0L, feedback.ContiguousThroughSequence);
            Assert.HasCount(1, feedback.MissingRanges);
            Assert.AreEqual(1L, feedback.MissingRanges[0].FromSequence);
            Assert.AreEqual(1L, feedback.MissingRanges[0].ToSequence);
            Assert.IsFalse(feedback.MissingRangesTruncated);
        }
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            Assert.IsFalse(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .Set<ImportedExecutionEvidenceUnit>().AnyAsync(u => u.BindingId == device.BindingId &&
                    u.EvidenceId == missing.EvidenceId).ConfigureAwait(false), "Hidden or missing dependencies cannot persist a terminal rejection.");
        }
        // A legitimately late proposal and acceptance still allow the exact previously pending bytes to converge.
        await SeedAssignmentAsync(device, missing.GraphRevision).ConfigureAwait(false);
        using var retry = await PostAsync(client, device, missing).ConfigureAwait(false);
        var acknowledged = await FeedbackAsync(retry).ConfigureAwait(false);
        Assert.HasCount(4, acknowledged.Facts);
        Assert.IsTrue(acknowledged.Facts.All(f => f.EvidenceId == missing.EvidenceId && f.PayloadSha256 == missing.PayloadSha256));
        Assert.AreEqual(ExecutionEvidenceFactKind.Acknowledged, acknowledged.Facts[^1].Kind);
        Assert.AreEqual(1L, acknowledged.ContiguousThroughSequence);
    }
}
