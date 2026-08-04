using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Minio;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class CentralTransientEventPersistenceIntegrationTests
{
    [TestMethod]
    public async Task PayloadReleaseFreshSealedPath_DiscoversTargetsOnlyDuringCreation()
    {
        await using var database = CreateDatabase("Issue284FreshSealed");
        try
        {
            var seed = await SeedIssue285EventAsync(database.Context).ConfigureAwait(false);
            var commands = new Issue284CommandRecorder();
            await using var measured = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlServer(database.ConnectionString)
                    .AddInterceptors(commands)
                    .Options);
            var handler = new Issue285RejectingHttpHandler();
            var service = CreateIssue285Service(measured, handler, seed.ArtifactIds);

            var result = await service.ReleaseAsync(
                CreateOwnerPrincipal("issue-284-fresh", admin: true),
                seed.EventId,
                seed.RowVersion,
                "issue-284-fresh",
                CancellationToken.None).ConfigureAwait(false);

            result.Status.Should().Be(CentralTransientPayloadReleaseStatus.Released);
            result.Response!.Replayed.Should().BeFalse();
            commands.CountContaining("CentralTransientObservationSources").Should().Be(1);
            commands.CountContaining("CentralTransientObservationBackgrounds").Should().Be(1);
            commands.CountContaining("CentralTransientDerivatives").Should().Be(1);
            commands.CountContainingAll(
                    "FROM [CentralArtifacts] WITH (UPDLOCK, HOLDLOCK)",
                    "[StorageReference]")
                .Should().Be(seed.ArtifactIds.Length);
            commands.CountContainingAll(
                    "FROM [CentralTransientPayloadReleaseItems]",
                    "FROM [CentralArtifacts]",
                    "FROM [CentralTransientDerivativeOutputIntents]")
                .Should().BePositive();
            commands.CountContainingAll(
                    "SELECT CAST(1 AS int) AS [Value] FROM [CentralArtifacts]",
                    "WITH (UPDLOCK, HOLDLOCK)")
                .Should().Be(0);
            handler.RequestCount.Should().Be(0);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task PayloadReleaseReplay_NormalizesIncompletePendingItemSet()
    {
        await using var database = CreateDatabase("Issue284ReplayNormalization");
        var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        Issue250ReleaseSeed? seed = null;
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            seed = await SeedIssue250ReleaseAsync(database.Context, minio, 1).ConfigureAwait(false);
            var release = await database.Context.CentralTransientPayloadReleases.AsNoTracking()
                .SingleAsync(item => item.ReleaseId == seed.ReleaseId).ConfigureAwait(false);
            var current = await database.Context.CentralTransientEventCurrent.AsNoTracking()
                .SingleAsync(item => item.CentralTransientEventId == release.CentralTransientEventId)
                .ConfigureAwait(false);
            await using var measured = CreateContext(database.ConnectionString);
            var service = CreateIssue250Service(measured, minio, TimeProvider.System);

            var result = await service.ReleaseAsync(
                CreateOwnerPrincipal(release.ActorIdentity, admin: true),
                release.CentralTransientEventId,
                current.RowVersion,
                release.IdempotencyKey,
                CancellationToken.None).ConfigureAwait(false);

            result.Status.Should().Be(CentralTransientPayloadReleaseStatus.Released);
            result.Response!.Replayed.Should().BeTrue();
            result.Response.ReleasedPayloadCount.Should().BeGreaterThan(1);
        }
        finally
        {
            if (seed is not null)
            {
                await CleanupIssue250ObjectsAsync(minio, seed).ConfigureAwait(false);
            }
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private sealed class Issue284CommandRecorder : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> commands = new();

        internal int CountContaining(string value)
            => commands.Count(command => command.Contains(value, StringComparison.Ordinal));

        internal int CountContainingAll(params string[] values)
            => commands.Count(command => values.All(value => command.Contains(value, StringComparison.Ordinal)));

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
