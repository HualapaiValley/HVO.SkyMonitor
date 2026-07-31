using System.Security.Claims;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Controllers;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.Tests.LogicHost.Controllers;

[TestClass]
[TestCategory("Unit")]
public sealed class DeploymentLocationProposalsControllerTests
{
    [TestMethod]
    public async Task ListAsync_UsesSingleAuthenticatedOwnerAndReturnsEtags()
    {
        var proposal = CreateProposal();
        var service = new CapturingAuthorityService { Proposals = [proposal] };
        var controller = CreateController(service, "owner-1");

        var result = await controller.ListAsync(DeploymentLocationResolutionStatus.Pending, 25);

        var response = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<IReadOnlyList<DeploymentLocationProposalsController.DeploymentLocationProposalResponse>>()
            .Subject;
        response.Should().ContainSingle(item => item.Id == proposal.Id
            && item.ETag == DeploymentLocationEtag.Create(proposal.ConcurrencyToken));
        service.OwnerUserId.Should().Be("owner-1");
        service.Status.Should().Be(DeploymentLocationResolutionStatus.Pending);
        service.Take.Should().Be(25);
    }

    [TestMethod]
    public async Task GetAsync_ForeignProposalIsHiddenAsNotFound()
    {
        var controller = CreateController(new CapturingAuthorityService(), "owner-1");

        var result = await controller.GetAsync(Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [TestMethod]
    public async Task ResolveAsync_RequiresStrongEtagAndMapsStaleMutation()
    {
        var service = new CapturingAuthorityService
        {
            ResolutionResult = new DeploymentLocationResolutionResult(
                DeploymentLocationMutationStatus.PreconditionFailed,
                null)
        };
        var controller = CreateController(service, "owner-1");
        var request = new DeploymentLocationProposalsController.DeploymentLocationResolutionRequest(
            DeploymentLocationResolutionStatus.Acknowledged,
            "owner-approved");

        var missing = await controller.ResolveAsync(Guid.NewGuid(), request, CancellationToken.None);
        ((ObjectResult)missing.Result!).StatusCode.Should().Be(StatusCodes.Status428PreconditionRequired);

        var token = Guid.NewGuid();
        controller.Request.Headers.IfMatch = DeploymentLocationEtag.Create(token);
        var stale = await controller.ResolveAsync(Guid.NewGuid(), request, CancellationToken.None);

        ((ObjectResult)stale.Result!).StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
        service.ExpectedConcurrencyToken.Should().Be(token);
        service.OwnerUserId.Should().Be("owner-1");
    }

    [TestMethod]
    public async Task ResolveAsync_MapsSupersededObservatoryEvaluationToConflict()
    {
        var service = new CapturingAuthorityService
        {
            ResolutionResult = new DeploymentLocationResolutionResult(
                DeploymentLocationMutationStatus.StaleAuthority,
                null)
        };
        var controller = CreateController(service, "owner-1");
        controller.Request.Headers.IfMatch = DeploymentLocationEtag.Create(Guid.NewGuid());

        var result = await controller.ResolveAsync(
            Guid.NewGuid(),
            new DeploymentLocationProposalsController.DeploymentLocationResolutionRequest(
                DeploymentLocationResolutionStatus.Acknowledged,
                "owner-approved"),
            CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [TestMethod]
    public void DeploymentLocationEtag_RejectsWeakMalformedAndEmptyTokens()
    {
        var token = Guid.NewGuid();
        var value = DeploymentLocationEtag.Create(token);

        DeploymentLocationEtag.TryParse(value, out var parsed).Should().BeTrue();
        parsed.Should().Be(token);
        DeploymentLocationEtag.TryParse($"W/{value}", out _).Should().BeFalse();
        DeploymentLocationEtag.TryParse("not-an-etag", out _).Should().BeFalse();
        DeploymentLocationEtag.TryParse(DeploymentLocationEtag.Create(Guid.Empty), out _).Should().BeFalse();
    }

    private static DeploymentLocationProposalsController CreateController(
        IDeploymentLocationAuthorityService service,
        string ownerUserId)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ownerUserId)], "test");
        return new DeploymentLocationProposalsController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    private static DeploymentLocationProposal CreateProposal()
    {
        var observatoryId = Guid.NewGuid();
        var observatory = ObservatoryLocationSnapshot.Create(
            observatoryId, 1, DateTimeOffset.UnixEpoch, 35, -113, 500, "America/Phoenix", 1000);
        var deployment = DeploymentLocationSnapshot.Create(
            "camera-one", 1, "gps", 3, DateTimeOffset.UnixEpoch, null,
            35.0001, -113, 501, "America/Phoenix");
        return new DeploymentLocationProposal(
            Guid.NewGuid(), Guid.NewGuid(), "camera-1", "Camera One", observatoryId, "Hualapai",
            observatory, deployment, DeploymentLocationSourceKind.Gps,
            DeploymentLocationResolutionStatus.Pending, "outside-observatory-boundary",
            DateTimeOffset.UnixEpoch, null, Guid.NewGuid());
    }

    private sealed class CapturingAuthorityService : IDeploymentLocationAuthorityService
    {
        public IReadOnlyList<DeploymentLocationProposal> Proposals { get; init; } = [];
        public DeploymentLocationResolutionResult ResolutionResult { get; init; } =
            new(DeploymentLocationMutationStatus.NotFound, null);
        public string? OwnerUserId { get; private set; }
        public DeploymentLocationResolutionStatus? Status { get; private set; }
        public int Take { get; private set; }
        public Guid ExpectedConcurrencyToken { get; private set; }

        public Task<DeploymentLocationProposalPage> ListAsync(
            string ownerUserId,
            DeploymentLocationResolutionStatus? status,
            int take,
            DeploymentLocationProposalCursor? cursor,
            Guid? observatoryScope = null,
            CancellationToken cancellationToken = default)
        {
            OwnerUserId = ownerUserId;
            Status = status;
            Take = take;
            return Task.FromResult(new DeploymentLocationProposalPage(Proposals, null));
        }

        public Task<DeploymentLocationProposal?> GetAsync(
            Guid deploymentLocationId,
            string ownerUserId,
            Guid? observatoryScope = null,
            CancellationToken cancellationToken = default)
        {
            OwnerUserId = ownerUserId;
            return Task.FromResult(Proposals.SingleOrDefault(item => item.Id == deploymentLocationId));
        }

        public Task<DeploymentLocationResolutionResult> ResolveAsync(
            Guid deploymentLocationId,
            string ownerUserId,
            DeploymentLocationResolutionStatus status,
            string reason,
            Guid expectedConcurrencyToken,
            Guid? observatoryScope = null,
            CancellationToken cancellationToken = default)
        {
            OwnerUserId = ownerUserId;
            Status = status;
            ExpectedConcurrencyToken = expectedConcurrencyToken;
            return Task.FromResult(ResolutionResult);
        }

        public Task<DeploymentLocationAcknowledgment> ProposeAsync(
            DeviceRegistration registration,
            DeploymentLocationSnapshot deployment,
            DeploymentLocationSourceKind sourceKind,
            string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ReconcileAsync(
            DeviceDeploymentLocationVersion deployment,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

    }
}
