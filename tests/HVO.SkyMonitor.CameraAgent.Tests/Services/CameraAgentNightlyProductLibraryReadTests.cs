using System.Diagnostics.CodeAnalysis;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

/// <summary>
/// The generated products library reads the run journal and the product catalog, which share no snapshot. These tests
/// drive the real catalog with a journal that changes between the two reads.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused MSTest methods.")]
public sealed class CameraAgentNightlyProductLibraryReadTests
{
    private static readonly NightlyProductLibraryQuery Query = new(NightlyProductProjectionContract.MaximumLibraryDates);

    [TestMethod]
    public async Task ARunThatSettlesBetweenTheJournalAndCatalogReadsIsShownAsItsRecordedResult()
    {
        await WithProducedSummaryAsync(async (catalog, running) =>
        {
            // The journal still held the run as Running; by the catalog read it had published and left the journal.
            var journal = new Journal([running], []);

            var entry = (await ReadAsync(catalog, journal)).Entries.Single();

            await AssertRecordedResultAsync(catalog, entry);
            Assert.AreEqual(2, journal.Reads, "One confirming read finds the change; an empty journal needs no further one.");
        });
    }

    [TestMethod]
    public async Task ARunThatIsStillRunningAfterTheCatalogReadIsShownAsRunning()
    {
        await WithProducedSummaryAsync(async (catalog, running) =>
        {
            var journal = new Journal([running]);

            var entry = (await ReadAsync(catalog, journal)).Entries.Single();

            // Active work still takes precedence over the published result of the same period.
            Assert.AreEqual(NightlyProductLibraryState.Running, entry.State);
            Assert.AreEqual(running.Run.Sequence, entry.Generation!.Sequence);
            Assert.IsNull(entry.Product);
            Assert.AreEqual(2, journal.Reads);
        });
    }

    [TestMethod]
    public async Task ARunThatFailsBetweenTheReadsIsSettledByItsOwnRecordedEvaluation()
    {
        await WithProducedSummaryAsync(async (catalog, running) =>
        {
            var failed = running with
            {
                Run = running.Run with { Outcome = LocalAutomationRunOutcome.Failed, CompletedAtUtc = running.Run.StartedAtUtc!.Value.AddMinutes(2) }
            };
            var journal = new Journal([running], [failed]);

            var entry = (await ReadAsync(catalog, journal)).Entries.Single();

            await AssertRecordedResultAsync(catalog, entry);
            Assert.AreEqual(3, journal.Reads, "The changed journal is confirmed once more after the second catalog read.");
        });
    }

    [TestMethod]
    public async Task AJournalThatNeverHoldsStillEndsTheReadAtItsBound()
    {
        await WithProducedSummaryAsync(async (catalog, running) =>
        {
            // Every read reports a newer attempt of the same occurrence, as an operator retrying in a loop would.
            var journal = new Journal(Enumerable.Range(1, 16).Select(sequence => (IReadOnlyList<NightlyProductGenerationAttempt>)
                [running with { Run = running.Run with { Sequence = sequence, Attempt = sequence } }]).ToArray());

            var entry = (await ReadAsync(catalog, journal)).Entries.Single();

            Assert.AreEqual(CameraAgentNightlyProductUiService.MaximumLibraryReads, journal.Reads);
            // The page is the latest pair read, not an error and not an unbounded wait.
            Assert.AreEqual(NightlyProductLibraryState.Running, entry.State);
            Assert.AreEqual(CameraAgentNightlyProductUiService.MaximumLibraryReads, entry.Generation!.Sequence);
        });
    }

    [TestMethod]
    public async Task AnIdleOrAbsentJournalIsReadAtMostOnce()
    {
        await WithProducedSummaryAsync(async (catalog, _) =>
        {
            var idle = new Journal(Array.Empty<NightlyProductGenerationAttempt>());
            await AssertRecordedResultAsync(catalog, (await ReadAsync(catalog, idle)).Entries.Single());
            Assert.AreEqual(1, idle.Reads, "Nothing is shown as running, so there is nothing to confirm.");

            await AssertRecordedResultAsync(catalog, (await ReadAsync(catalog, journal: null)).Entries.Single());
        });
    }

    [TestMethod]
    public async Task AFailedConfirmingReadIsReportedAsUnavailableRatherThanAsAStalePage()
    {
        await WithProducedSummaryAsync(async (catalog, running) =>
        {
            var journal = new Journal([running]) { FailAfter = 1 };

            var result = await ReadResultAsync(catalog, journal);

            Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
            Assert.IsNull(result.Value);
            Assert.AreEqual(2, journal.Reads);
        });
    }

    /// <summary>
    /// The entry is the catalog's own recorded result, with no generation attempt laid over it. The fixture admits one
    /// preview of a whole sunrise day, which the catalog records as published with partial coverage.
    /// </summary>
    private static async Task AssertRecordedResultAsync(INightlyProductCatalog catalog, NightlyProductLibraryEntry entry)
    {
        var recorded = (await catalog.ListLibraryAsync(Query, CancellationToken.None, [])).Entries.Single();
        Assert.AreEqual(NightlyProductLibraryState.Partial, recorded.State);
        Assert.IsNotNull(recorded.Product);

        Assert.AreEqual(recorded.State, entry.State);
        Assert.IsNull(entry.Generation);
        Assert.AreEqual(recorded.Product.ProductId, entry.Product?.ProductId);
    }

    private static async Task<NightlyProductLibraryPage> ReadAsync(INightlyProductCatalog catalog, Journal? journal)
    {
        var result = await ReadResultAsync(catalog, journal);
        Assert.IsNotNull(result.Value, $"{result.Kind}: {result.Message}");
        return result.Value;
    }

    private static async Task<OperatorUiResult<NightlyProductLibraryPage>> ReadResultAsync(INightlyProductCatalog catalog, Journal? journal)
    {
        using var context = new BunitContext();
        var authorization = context.AddAuthorization();
        authorization.SetAuthorized("owner");
        authorization.SetPolicies(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        var service = new CameraAgentNightlyProductUiService(
            context.Services.GetRequiredService<AuthenticationStateProvider>(), context.Services.GetRequiredService<IAuthorizationService>(),
            catalog, NightlyProductFixture.HostOptions(Path.GetTempPath(), NightlyProductFixture.Options()),
            NullLogger<CameraAgentNightlyProductUiService>.Instance, automation: journal?.Store);
        return await service.ListLibraryAsync(Query, CancellationToken.None);
    }

    /// <summary>
    /// A real catalog holding one published daily summary, and the journal's view of the run that produced it while
    /// that run was still executing: started before the evaluation, not yet completed.
    /// </summary>
    private static async Task WithProducedSummaryAsync(Func<INightlyProductCatalog, NightlyProductGenerationAttempt, Task> test)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-library-read");
        try
        {
            var options = NightlyProductFixture.HostOptions(root, NightlyProductFixture.Options());
            var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
            var started = clock.UtcNow;
            var reader = new InMemoryNightlySourceReader();
            reader.Add(NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(10)));
            using var catalog = new SqliteNightlyProductStore(options, clock);
            using var generator = new NightlyProductGenerator(options, new FixedConfigurationAccessor(NightlyProductFixture.Configuration()),
                reader, catalog, new HVO.SkyMonitor.Astronomy.AstronomyEnginePlanetEphemeris(), static () => null, clock);
            var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.DailySummary);
            clock.UtcNow = started.AddMinutes(1);
            await generator.RunAsync(occurrence, CancellationToken.None);
            Assert.HasCount(1, await catalog.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None));
            var running = new NightlyProductGenerationAttempt(new LocalAutomationRun(1, occurrence.Definition.DefinitionId, "fixture-run",
                LocalAutomationContract.ComputeRevisionSha256(occurrence.Definition), LocalAutomationTriggerKind.SourceWindowClosed,
                started, started, null, LocalAutomationRunOutcome.Running, string.Empty, null)
            { Occurrence = occurrence }, Enabled: true);
            await test(catalog, running);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>A journal whose successive reads return the given attempts; the last answer repeats.</summary>
    private sealed class Journal
    {
        private readonly IReadOnlyList<NightlyProductGenerationAttempt>[] _answers;

        internal Journal(params IReadOnlyList<NightlyProductGenerationAttempt>[] answers)
        {
            _answers = answers;
            var store = new Mock<ILocalAutomationStore>(MockBehavior.Strict);
            store.Setup(journal => journal.ListNightlyProductRunsAsync(Query, It.IsAny<CancellationToken>())).Returns(() =>
            {
                var read = Reads++;
                return read >= FailAfter
                    ? throw new IOException("Synthetic journal failure.")
                    : ValueTask.FromResult(_answers[Math.Min(read, _answers.Length - 1)]);
            });
            Store = store.Object;
        }

        internal ILocalAutomationStore Store { get; }

        internal int Reads { get; private set; }

        internal int FailAfter { get; init; } = int.MaxValue;
    }
}
