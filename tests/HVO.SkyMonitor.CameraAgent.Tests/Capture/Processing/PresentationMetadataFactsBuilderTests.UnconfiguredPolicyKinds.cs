using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

// Presentation callers keep a selected kind that has no configured source as missing, instead of requesting it and
// failing every capture with the association-policy error. Refs #1784
public sealed partial class PresentationMetadataFactsBuilderTests
{
    private static readonly EnvironmentalObservationKind[] DefaultAnnotationKinds =
        [.. new AnnotationProcessingStepOptions().EnvironmentalKinds];

    private static readonly string[] DefaultKindsMissing =
    [
        "AIRTEMPERATURE MISSING",
        "RELATIVEHUMIDITY MISSING",
        "ATMOSPHERICPRESSURE MISSING",
        "WINDSPEED MISSING",
        "RAINSTATE MISSING",
        "CLOUDCOVER MISSING"
    ];

    private static readonly string[] UnconfiguredSelectionFactKinds = ["AirTemperature", "RainState"];

    private const string FreshTemperatureLine = "AIRTEMPERATURE 21.5 C FRESH AGE 5 S";

    [TestMethod]
    public async Task AnnotationEnvironmentLinesReadOnlyConfiguredKindsAndKeepTheRestMissing()
    {
        // NWS-only shape: AirTemperature is the one configured kind among the six default selected kinds.
        using var store = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch.AddMinutes(1)));
        await CommitAsync(store, Fact(Guid.Parse("17840000-0000-0000-0000-000000000001"),
            EnvironmentalObservationKind.AirTemperature, 21.5)).ConfigureAwait(false);
        var acquisition = EnvironmentSelectionAcquisition([EnvironmentalObservationKind.AirTemperature]);
        var fixture = await CreateFixtureAsync(store, acquisition).ConfigureAwait(false);
        await AssociateAirTemperatureAsync(fixture).ConfigureAwait(false);
        var reads = new CountingAssociationStore(store);
        using var annotation = CreateAnnotationStep(store, reads, acquisition, DefaultAnnotationKinds);

        var lines = await annotation.Step.CreateEnvironmentLinesAsync(fixture.Descriptor, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(lines);
        CollectionAssert.AreEqual(new[] { FreshTemperatureLine }.Concat(DefaultKindsMissing.Skip(1)).ToArray(), lines.ToArray());
        Assert.AreEqual(1, reads.ReadCount, "The configured kind is still read from its durable capture association.");
    }

    [TestMethod]
    public async Task AnnotationEnvironmentLinesWithNoSelectedKindConfiguredDoNotReadAssociations()
    {
        // SQM-only shape: none of the six default selected kinds has a configured source.
        using var store = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch.AddMinutes(1)));
        var acquisition = EnvironmentSelectionAcquisition([EnvironmentalObservationKind.SkyQuality]);
        var fixture = await CreateFixtureAsync(store, acquisition).ConfigureAwait(false);
        _ = await fixture.Associations.AssociateAsync(
            fixture.Descriptor.Capture.CaptureId, fixture.Descriptor.Capture.CaptureSequence,
            fixture.Descriptor.Timing.ExposureStartedUtc, fixture.Descriptor.Timing.ExposureEndedUtc,
            fixture.Descriptor.Capture.RigId, [EnvironmentalObservationKind.SkyQuality], CancellationToken.None)
            .ConfigureAwait(false);
        var reads = new CountingAssociationStore(store);
        using var annotation = CreateAnnotationStep(store, reads, acquisition, DefaultAnnotationKinds);

        var lines = await annotation.Step.CreateEnvironmentLinesAsync(fixture.Descriptor, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(lines);
        CollectionAssert.AreEqual(DefaultKindsMissing, lines.ToArray());
        Assert.AreEqual(0, reads.ReadCount);
    }

    [TestMethod]
    public async Task AnnotationEnvironmentLinesInsideTheConfiguredPolicyAreUnchanged()
    {
        // Control: every selected kind is configured, so the full selection is requested as before.
        using var store = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch.AddMinutes(1)));
        await CommitAsync(store, Fact(Guid.Parse("17840000-0000-0000-0000-000000000002"),
            EnvironmentalObservationKind.AirTemperature, 21.5)).ConfigureAwait(false);
        var acquisition = EnvironmentSelectionAcquisition(DefaultAnnotationKinds);
        var fixture = await CreateFixtureAsync(store, acquisition).ConfigureAwait(false);
        _ = await fixture.Associations.AssociateAsync(
            fixture.Descriptor.Capture.CaptureId, fixture.Descriptor.Capture.CaptureSequence,
            fixture.Descriptor.Timing.ExposureStartedUtc, fixture.Descriptor.Timing.ExposureEndedUtc,
            fixture.Descriptor.Capture.RigId, DefaultAnnotationKinds, CancellationToken.None).ConfigureAwait(false);
        var reads = new CountingAssociationStore(store);
        using var annotation = CreateAnnotationStep(store, reads, acquisition, DefaultAnnotationKinds);

        var lines = await annotation.Step.CreateEnvironmentLinesAsync(fixture.Descriptor, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(lines);
        CollectionAssert.AreEqual(new[] { FreshTemperatureLine }.Concat(DefaultKindsMissing.Skip(1)).ToArray(), lines.ToArray());
        Assert.AreEqual(1, reads.ReadCount);
    }

    [TestMethod]
    public async Task AnnotationEnvironmentLinesStillWaitForAPendingConfiguredAssociation()
    {
        using var store = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch.AddMinutes(1)));
        var acquisition = EnvironmentSelectionAcquisition([EnvironmentalObservationKind.AirTemperature]);
        var fixture = await CreateFixtureAsync(store, acquisition).ConfigureAwait(false);
        var reads = new CountingAssociationStore(store);
        using var annotation = CreateAnnotationStep(store, reads, acquisition, DefaultAnnotationKinds);

        // The association lane has not completed for this capture yet.
        var pending = await annotation.Step.CreateEnvironmentLinesAsync(fixture.Descriptor, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNull(pending);
        Assert.AreEqual(1, reads.ReadCount);

        // Recovery: once the lane completes, the same capture renders.
        await AssociateAirTemperatureAsync(fixture).ConfigureAwait(false);
        var recovered = await annotation.Step.CreateEnvironmentLinesAsync(fixture.Descriptor, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNotNull(recovered);
        CollectionAssert.AreEqual(DefaultKindsMissing, recovered.ToArray());
    }

    [TestMethod]
    public async Task AnnotationEnvironmentLinesStillRejectConflictingDurablePolicies()
    {
        using var store = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch.AddMinutes(1)));
        var acquisition = EnvironmentSelectionAcquisition([EnvironmentalObservationKind.AirTemperature]);
        var fixture = await CreateFixtureAsync(store, acquisition).ConfigureAwait(false);
        await AssociateAirTemperatureAsync(fixture).ConfigureAwait(false);
        var original = (await store.ReadAssociationsAsync(_root!, fixture.Descriptor.Capture.CaptureId, CancellationToken.None)
            .ConfigureAwait(false)).Single();
        await store.SaveAssociationAsync(_root!, original with
        {
            PolicyIdentitySha256 = new string('B', 64),
            AssociationIdentitySha256 = new string('C', 64)
        }, CancellationToken.None).ConfigureAwait(false);
        using var annotation = CreateAnnotationStep(store, store, acquisition, DefaultAnnotationKinds);

        var exception = await Assert.ThrowsExactlyAsync<EnvironmentalObservationIdentityConflictException>(async () =>
            await annotation.Step.CreateEnvironmentLinesAsync(fixture.Descriptor, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
        Assert.AreEqual("Multiple durable environmental association policies satisfy the requested subset.", exception.Message);
    }

    [TestMethod]
    public async Task FrameFactsSelectionOutsideThePolicyRetainsMissingFactsLikeDisabledAcquisition()
    {
        using var store = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch.AddMinutes(1)));
        var temperature = Fact(Guid.Parse("17840000-0000-0000-0000-000000000003"),
            EnvironmentalObservationKind.AirTemperature, 21.5);
        await CommitAsync(store, temperature).ConfigureAwait(false);
        var acquisition = EnvironmentSelectionAcquisition([EnvironmentalObservationKind.AirTemperature]);
        var fixture = await CreateFixtureAsync(store, acquisition).ConfigureAwait(false);
        await AssociateAirTemperatureAsync(fixture).ConfigureAwait(false);
        var persisted = (await store.ReadAssociationsAsync(_root!, fixture.Descriptor.Capture.CaptureId, CancellationToken.None)
            .ConfigureAwait(false)).Single();
        RegisterEnvironmentDependencies(fixture);
        var reads = new CountingAssociationStore(store);
        var builder = CreateBuilder(store, reads, acquisition);
        EnvironmentalObservationKind[] selection = [EnvironmentalObservationKind.RainState, EnvironmentalObservationKind.AirTemperature];

        var options = new FrameFactsPresentationProcessingStepOptions
        {
            WidthPixels = 1936,
            HeightPixels = 1216,
            EnvironmentalKinds = selection
        };
        fixture.Context.BeginNode("facts", ["scene", "preview"]);
        await new FrameFactsPresentationCaptureProcessingStep(new("facts", FrameFactsPresentation.StepAlias, 72), options, builder)
            .ProcessAsync(fixture.Context, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, reads.ReadCount);
        var outcome = fixture.Context.ProcessingOutcomes[^1];
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        Assert.HasCount(2, outcome.Products, "Both the facts product and the frame-facts layer are produced.");
        var facts = PresentationProcessingProducts.ParseMetadataFacts(
            outcome.Products.Single(product => product.Variant == options.FactsOutputVariant).Payload);
        Assert.IsNotNull(facts.FrameFacts);
        CollectionAssert.AreEqual(UnconfiguredSelectionFactKinds, facts.Environment.Select(static fact => fact.Kind).ToArray());
        var air = facts.Environment[0];
        Assert.AreEqual("Fresh", air.Status);
        Assert.AreEqual(temperature.ObservationId, air.ObservationId);
        Assert.AreEqual(persisted.AssociationIdentitySha256, air.AssociationIdentitySha256);
        Assert.AreEqual(persisted.PolicyIdentitySha256, air.PolicyIdentitySha256);
        var rain = facts.Environment[1];
        Assert.AreEqual("Missing", rain.Status);
        Assert.IsNull(rain.ObservationId);
        Assert.AreEqual(EnvironmentalAssociationService.CreatePolicyIdentity(
            [EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationKind.RainState]), rain.PolicyIdentitySha256);

        // The unconfigured kind is retained exactly as acquisition-disabled retains it.
        fixture.Context.BeginNode("facts-disabled", []);
        var disabled = await CreateBuilder(store, store, new EnvironmentalAcquisitionOptions()).BuildAsync(
            fixture.Context, fixture.Scene, fixture.Stack, SourceIds(fixture), selection, CancellationToken.None,
            retainFrameFacts: true).ConfigureAwait(false);
        Assert.IsNotNull(disabled);
        var disabledRain = disabled.Environment.Single(static fact => fact.Kind == "RainState");
        Assert.AreEqual(disabledRain.AssociationIdentitySha256, rain.AssociationIdentitySha256);
        Assert.AreEqual(disabledRain.PolicyIdentitySha256, rain.PolicyIdentitySha256);
        Assert.AreEqual(disabledRain.DisplayLine, rain.DisplayLine);
    }

    [TestMethod]
    public async Task FrameFactsSelectionWithNoConfiguredKindDoesNotReadAssociations()
    {
        using var store = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch.AddMinutes(1)));
        var acquisition = EnvironmentSelectionAcquisition([EnvironmentalObservationKind.SkyQuality]);
        var fixture = await CreateFixtureAsync(store, acquisition).ConfigureAwait(false);
        var reads = new CountingAssociationStore(store);
        fixture.Context.BeginNode("facts", []);

        var facts = await CreateBuilder(store, reads, acquisition).BuildAsync(
            fixture.Context, fixture.Scene, fixture.Stack, SourceIds(fixture), [EnvironmentalObservationKind.RainState],
            CancellationToken.None, retainFrameFacts: true).ConfigureAwait(false);

        Assert.IsNotNull(facts, "No configured kind is requested, so nothing is pending.");
        Assert.AreEqual(0, reads.ReadCount);
        Assert.AreEqual("Missing", facts.Environment.Single().Status);
        Assert.AreEqual("RainState", facts.Environment.Single().Kind);
    }

    [TestMethod]
    public void ConfiguredPolicyKindsAreTheSortedDistinctConfiguredSubset()
    {
        var acquisition = EnvironmentSelectionAcquisition(
            [EnvironmentalObservationKind.RainState, EnvironmentalObservationKind.AirTemperature]);

        CollectionAssert.AreEqual(
            new[] { EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationKind.RainState },
            EnvironmentalAssociationService.SelectConfiguredPolicyKinds(acquisition,
            [
                EnvironmentalObservationKind.RainState, EnvironmentalObservationKind.CloudCover,
                EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationKind.RainState
            ]));
        Assert.IsEmpty(EnvironmentalAssociationService.SelectConfiguredPolicyKinds(acquisition, [EnvironmentalObservationKind.SkyQuality]));
        // With no configured source the policy does not constrain the request, matching ReadCompletedAsync.
        CollectionAssert.AreEqual(
            new[] { EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationKind.SkyQuality },
            EnvironmentalAssociationService.SelectConfiguredPolicyKinds(new EnvironmentalAcquisitionOptions { Enabled = true },
                [EnvironmentalObservationKind.SkyQuality, EnvironmentalObservationKind.AirTemperature]));
    }

    private PresentationMetadataFactsBuilder CreateBuilder(
        SqliteEnvironmentalObservationOutbox store,
        ILocalEnvironmentalAssociationStore associations,
        EnvironmentalAcquisitionOptions acquisition)
    {
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root!, EnvironmentalAcquisition = acquisition });
        return new PresentationMetadataFactsBuilder(
            new EnvironmentalAssociationService(store, associations, options, new FixedTimeProvider(Epoch.AddMinutes(1))),
            store,
            options);
    }

    private AnnotationHarness CreateAnnotationStep(
        SqliteEnvironmentalObservationOutbox store,
        ILocalEnvironmentalAssociationStore associations,
        EnvironmentalAcquisitionOptions acquisition,
        IReadOnlyList<EnvironmentalObservationKind> kinds)
    {
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root!, EnvironmentalAcquisition = acquisition });
        var provider = new ServiceCollection()
            .AddSingleton(options)
            .AddSingleton<ILocalEnvironmentalObservationStore>(store)
            .AddSingleton(new EnvironmentalAssociationService(
                store, associations, options, new FixedTimeProvider(Epoch.AddMinutes(1))))
            .BuildServiceProvider();
        var step = new AnnotationCaptureProcessingStep(
            new CaptureProcessingStepMetadata("annotation", "Annotation", 75),
            new AnnotationProcessingStepOptions { DrawMetadataCorners = true, EnvironmentalKinds = kinds },
            new ProjectedSceneStore(),
            new UnexpectedSceneProvider(),
            new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()),
            provider);
        return new AnnotationHarness(step, provider);
    }

    private sealed class AnnotationHarness(AnnotationCaptureProcessingStep step, ServiceProvider provider) : IDisposable
    {
        internal AnnotationCaptureProcessingStep Step { get; } = step;

        public void Dispose() => provider.Dispose();
    }

    private sealed class UnexpectedSceneProvider : IAnnotationSceneProvider
    {
        public ValueTask<AnnotationSceneResult> BuildAsync(
            CameraModuleConfig config,
            ReconstructionDescriptor? descriptor,
            CameraFrame rawFrame,
            IReadOnlyList<string> constellationIds,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<AnnotationSceneResult>(
                new InvalidOperationException("Environment lines never build an annotation scene."));
    }

    /// <summary>Delegates to the durable store and counts association reads.</summary>
    private sealed class CountingAssociationStore(ILocalEnvironmentalAssociationStore inner) : ILocalEnvironmentalAssociationStore
    {
        private int _reads;

        internal int ReadCount => Volatile.Read(ref _reads);

        public ValueTask<EnvironmentalCaptureAvailability> ReadCaptureAvailabilityAsync(
            string root, Guid captureId, Guid? acquisitionAttemptId, CancellationToken cancellationToken)
            => inner.ReadCaptureAvailabilityAsync(root, captureId, acquisitionAttemptId, cancellationToken);

        public ValueTask SaveAssociationAsync(
            string root, LocalEnvironmentalCaptureAssociation association, CancellationToken cancellationToken)
            => inner.SaveAssociationAsync(root, association, cancellationToken);

        public ValueTask SaveAssociationSetAsync(
            string root, IReadOnlyList<LocalEnvironmentalCaptureAssociation> associations, CancellationToken cancellationToken)
            => inner.SaveAssociationSetAsync(root, associations, cancellationToken);

        public ValueTask<IReadOnlyList<LocalEnvironmentalCaptureAssociation>> ReadAssociationsAsync(
            string root, Guid captureId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            return inner.ReadAssociationsAsync(root, captureId, cancellationToken);
        }
    }
}
