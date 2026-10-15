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
        var assignment = revision.Assignment!;
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.CentralProcessingGraphRevisions.Add(new CentralProcessingGraphRevision
        {
            Id = assignment.CatalogRevisionId,
            Name = "Evidence fixture",
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
}
