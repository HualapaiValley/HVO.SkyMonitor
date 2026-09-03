using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CentralProcessingOverrideIntegrationTests
{
    [TestMethod]
    public async Task SqlServer_OwnerOverrideIsVersionedImmutableAndChangesResolvedFutureRecipe()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var marker = Guid.NewGuid().ToString("N");
        var observatory = await services.GetRequiredService<IObservatoryService>().CreateOrUpdateAsync(
            new ObservatoryUpsertRequest(
                null,
                owner.Id,
                $"Processing override {marker}",
                19.8,
                -155.4,
                4200,
                "Pacific/Honolulu",
                true)).ConfigureAwait(false);
        var policy = services.GetRequiredService<ICentralProcessingPolicyService>();

        var first = await policy.SetAsync(
            observatory.Id,
            owner.Id,
            new CentralProcessingPolicyRequest(620_000, false, "integration-override")).ConfigureAwait(false);
        var recipes = await policy.ResolveRequiredRecipesAsync(
            observatory.Id,
            FrameArtifactRole.Raw,
            CancellationToken.None).ConfigureAwait(false);
        var second = await policy.SetAsync(
            observatory.Id,
            owner.Id,
            new CentralProcessingPolicyRequest(700_000, null, "integration-successor")).ConfigureAwait(false);

        first.Should().Be(CentralProcessingPolicyMutationOutcome.Applied);
        second.Should().Be(CentralProcessingPolicyMutationOutcome.Applied);
        var cloud = recipes.Single(recipe => recipe.RecipeName == BuiltInProcessingRecipes.CloudAssessment);
        cloud.Options.GetRawText().Should().Contain("620000");
        var history = await db.CentralProcessingOverrideVersions
            .Where(item => item.ObservatoryId == observatory.Id)
            .OrderBy(item => item.Version)
            .ToArrayAsync().ConfigureAwait(false);
        history.Should().HaveCount(2);
        history[0].SupersededAtUtc.Should().NotBeNull();
        history[1].CloudTransmissionThresholdMillionths.Should().Be(700_000);

        history[0].ReasonCode = "tampered";
        Func<Task> mutate = () => db.SaveChangesAsync();
        var exception = await mutate.Should().ThrowAsync<DbUpdateException>();
        exception.Which.InnerException!.Message.Should()
            .Contain("Central processing override history is immutable");
    }
}
