using System.Reflection;
using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TestContext = Microsoft.VisualStudio.TestTools.UnitTesting.TestContext;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class CenteredPipelineEvidenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 4, 0, 0, TimeSpan.Zero);
    private static readonly string Checksum = new('A', 64);
    private static readonly CenteredViewStream Stream = new(new("test-agent", "sky"), new Guid("00000000-0000-0000-0000-000000000001"), Checksum);
    private const string Satellites = "predicted-satellites";

    [TestMethod]
    [DataRow(false, "Mono16 (mono)")]
    [DataRow(true, "BayerRggb16 (color)")]
    public void RunningEvidenceShowsCapture105Reference103AndFourOfFiveAssessed(bool bayer, string format)
    {
        var captures = Exposures(bayer);
        var layers = captures.Select((source, i) => Layer(source, i == 4 ? CenteredViewReadiness.Waiting : CenteredViewReadiness.AssessedEmpty)).ToArray();
        var view = View(captures[2], captures[..5], layers[..5]);
        var head = captures[4];
        var runtime = Runtime(head, CenteredRuntimeEvidenceBasis.ActiveAcquisition);
        var snapshot = new CenteredPipelineRunningSnapshot(Stream.Camera, Acquisition: runtime, Head: head.Input,
            HeadExecutionId: head.ExecutionId, Display: Current(view), PendingLayers: new HashSet<CenteredViewLayerKey> { layers[4].Key });

        using var context = new BunitContext();
        var cut = context.Render<CenteredRunningEvidence>(parameters => parameters.Add(component => component.Snapshot, snapshot));
        Assert.Contains("Capture #105", cut.Find("[aria-label='Acquisition and buffer']").TextContent);
        Assert.Contains("Capture #103", cut.Find("[aria-label='Live display publication'] .publication-facts").TextContent);
        Assert.Contains("4/5 assessed", cut.Find("[data-layer-kind='predicted-satellites'] summary").TextContent);
        var lastMember = cut.FindAll("[data-layer-kind='predicted-satellites'] tbody tr")[4];
        Assert.AreEqual("Waiting", lastMember.Children[1].TextContent);
        Assert.Contains("Exact source producer is queued, running, or awaiting retry", lastMember.TextContent);
        Assert.Contains(format, cut.Markup);
        Assert.Contains(bayer ? "origin (1, 1)" : "No CFA", cut.Markup);
        Assert.Contains("Source to reference mapping", cut.Markup);
        Assert.Contains(nameof(CenteredRegistrationMethod.VerifiedCentroidSimilarity), cut.Markup);
        Assert.AreEqual(5, cut.FindAll("[aria-label='Centered window'] tbody tr").Count);
        Assert.AreEqual(CenteredViewLinks.Revision(captures[2].Capture.CaptureId, view.Base.BaseId, view.Revision.RevisionId),
            cut.FindAll("a").Single(anchor => anchor.TextContent.Contains("Open exact", StringComparison.Ordinal)).GetAttribute("href"));
    }

    [TestMethod]
    public void UnsetAcquisitionTimestampRendersUnavailableInPhoenix()
    {
        var head = Exposures(false)[4];
        var acquisition = Runtime(head, CenteredRuntimeEvidenceBasis.ActiveAcquisition) with { ObservedUtc = default };
        var snapshot = new CenteredPipelineRunningSnapshot(Stream.Camera, Acquisition: acquisition, Head: head.Input);
        using var context = new BunitContext();
        context.Services.AddSingleton<IObservingDayCalendarProvider>(
            new FixedObservingDayCalendarProvider(ObservingDayCalendar.Create("America/Phoenix")));

        var cut = context.Render<CenteredRunningEvidence>(parameters => parameters.Add(component => component.Snapshot, snapshot));

        var facts = cut.Find("[aria-label='Acquisition and buffer']");
        var observed = facts.QuerySelectorAll("dt").Single(term => term.TextContent == "Observed");
        Assert.AreEqual("Unavailable", observed.NextElementSibling?.TextContent);
        Assert.Contains("-07:00 (America/Phoenix)", facts.TextContent);
    }

    [TestMethod]
    [DataRow("exposure-start-outside-tolerance", "exposure-start-outside-tolerance")]
    [DataRow("processing-deadline-expired", "processing-deadline-expired")]
    [DataRow("linear.no-configured-step", "linear.no-configured-step")]
    [DataRow("calendar-predecessor-unavailable", "calendar-predecessor-unavailable")]
    [DataRow("centered.product-failed/var/lib/hvo/raw", "Source production failed")]
    [DataRow("linear.not-a-real-producer-code", "Withheld")]
    [DataRow("registration.resource-limit.extra", "Withheld")]
    [DataRow("registration.unknown/var/lib/hvo/raw", "Withheld")]
    [DataRow("/var/lib/hvo/raw", "Withheld")]
    [DataRow("ghp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "Withheld")]
    [DataRow("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.AAAAAAAAA", "Withheld")]
    [DataRow("password=test-credential-for-redaction", "Withheld")]
    public void PublicationReasonsPreserveKnownCodesAndWithholdSensitiveDetails(string value, string expected)
        => Assert.AreEqual(expected, CenteredPublicationEvidence.Reason(value));

    [TestMethod]
    [DataRow(CenteredRegisteredStackRecipe.ResourceLimit)]
    [DataRow(CenteredRegisteredStackRecipe.InvalidPlan)]
    [DataRow(CenteredRegisteredStackRecipe.ReferenceUnavailable)]
    [DataRow(CenteredRegisteredStackRecipe.RetainedInputsUnavailable)]
    [DataRow("registration.failed")]
    [DataRow(ProcessingReasonCodes.ExecutionFailed)]
    [DataRow(ProcessingReasonCodes.InvalidInput)]
    [DataRow(ProcessingReasonCodes.InvalidLineage)]
    [DataRow("processing.live-deadline")]
    [DataRow("processing.live-maximum-age")]
    [DataRow("processing.plan-superseded")]
    [DataRow("processing.storage-policy-mismatch-scene-unavailable")]
    [DataRow("metering.source-evidence-unavailable")]
    [DataRow("linear.plan-did-not-produce")]
    [DataRow("linear.execution-expired")]
    [DataRow("linear.invalid-levels")]
    [DataRow("linear.no-output")]
    [DataRow(FrameWhiteThreshold.UnavailableReason)]
    [DataRow(FrameWhiteThreshold.OpaqueAdcReason)]
    [DataRow("product.availability-visibility-unproven")]
    [DataRow(ProcessingReasonCodes.CalibrationReferenceConditionsMismatch)]
    [DataRow(CalibrationLibraryReasonCodes.IncompatibleCodeSpace)]
    [DataRow("astrometry.scene-time-calibration-mismatch")]
    [DataRow(CloudAssessmentReasonCodes.InsufficientValidSupport)]
    [DataRow("normalization.plan.non-right-aligned-response")]
    [DataRow(GainNormalizationReasonCodes.PhysicalResponseNotEnabled)]
    public void PublicationReasonsShowProducerCodeAndRemovePrivateDetail(string code)
    {
        Assert.AreEqual(code, CenteredPublicationEvidence.Reason(code));
        Assert.AreEqual(code, CenteredPublicationEvidence.Reason($"{code}/var/lib/hvo/raw"));
    }

    [TestMethod]
    [DataRow(nameof(CaptureSatelliteReason.NamedPassPredictionLineageUnavailable))]
    [DataRow(nameof(CaptureSatelliteReason.NamedPassPredictionTimingUnavailable))]
    [DataRow(nameof(CaptureSatelliteReason.NamedPassPredictionTimingMismatch))]
    [DataRow(nameof(CaptureSatelliteReason.NamedPassPredictionInputsMismatch))]
    [DataRow(nameof(CaptureSatelliteReason.TimeAvailabilityUnknown))]
    [DataRow(nameof(CaptureSatelliteReason.TimeNotOriginallyAvailable))]
    [DataRow(nameof(CaptureSatelliteReason.ReadoutGeometryUnavailable))]
    [DataRow(nameof(CaptureSatelliteReason.CaptureLocationUnavailable))]
    [DataRow(nameof(CaptureSatelliteReason.PinnedSnapshotUnavailable))]
    [DataRow(nameof(CaptureSatelliteReason.PinnedTimeInputsUnavailable))]
    [DataRow(nameof(CaptureSatelliteReason.TimeCoverageUnavailable))]
    [DataRow(nameof(CaptureSatelliteReason.ExposureWindowBudget))]
    [DataRow(nameof(CaptureSatelliteReason.CaptureReadoutProfileMismatch))]
    [DataRow(nameof(CaptureSatelliteReason.SnapshotNotOriginallyAvailable))]
    [DataRow(nameof(CaptureSatelliteReason.SelectionCandidateBudget))]
    [DataRow(nameof(CaptureSatelliteReason.PinnedRecordSelectionMismatch))]
    [DataRow(nameof(CaptureSatelliteReason.SnapshotRetiredBeforePin))]
    [DataRow(nameof(CaptureSatelliteReason.NoExposureSegments))]
    [DataRow(nameof(OrbitalSnapshotUnavailableReason.InvalidIdentity))]
    [DataRow(nameof(OrbitalSnapshotUnavailableReason.NotConfigured))]
    [DataRow(nameof(OrbitalSnapshotUnavailableReason.NoActiveSnapshot))]
    [DataRow(nameof(OrbitalSnapshotUnavailableReason.UnknownIdentity))]
    [DataRow(nameof(OrbitalSnapshotUnavailableReason.Retired))]
    [DataRow(nameof(OrbitalSnapshotUnavailableReason.IntegrityFailure))]
    [DataRow(nameof(OrbitalSnapshotUnavailableReason.StorageUnavailable))]
    [DataRow(nameof(OrbitalPredictionReason.None))]
    [DataRow(nameof(OrbitalPredictionReason.ObjectMissing))]
    [DataRow(nameof(OrbitalPredictionReason.SelectionWithheld))]
    [DataRow(nameof(OrbitalPredictionReason.PropagationFailure))]
    [DataRow(nameof(OrbitalPredictionReason.InvalidGeometry))]
    [DataRow(nameof(OrbitalPredictionReason.MissingEarthOrientation))]
    [DataRow(nameof(OrbitalPredictionReason.UnsupportedLeapCrossing))]
    [DataRow(nameof(OrbitalPredictionReason.EvaluationBudget))]
    [DataRow(nameof(OrbitalPredictionReason.SampleBudget))]
    [DataRow(nameof(OrbitalPredictionReason.ResultBudget))]
    [DataRow(nameof(OrbitalPredictionReason.Deadline))]
    [DataRow(nameof(OrbitalPredictionReason.IncompleteInterval))]
    [DataRow(CaptureSatelliteProducts.TimeInputsUnavailablePrefix + nameof(CameraAgentOrbitalTimeDataProblem.Disabled))]
    [DataRow(CaptureSatelliteProducts.TimeInputsUnavailablePrefix + nameof(CameraAgentOrbitalTimeDataProblem.OverrideIncomplete))]
    [DataRow(CaptureSatelliteProducts.TimeInputsUnavailablePrefix + nameof(CameraAgentOrbitalTimeDataProblem.OverrideInvalid))]
    [DataRow(CaptureSatelliteProducts.TimeInputsUnavailablePrefix + nameof(CameraAgentOrbitalTimeDataProblem.BundledInvalid))]
    [DataRow(CaptureSatelliteProducts.PresentationWithheldPrefix + nameof(OrbitalPredictionReason.None))]
    [DataRow(CaptureSatelliteProducts.PresentationWithheldPrefix + nameof(OrbitalPredictionReason.ObjectMissing))]
    [DataRow(CaptureSatelliteProducts.PresentationWithheldPrefix + nameof(OrbitalPredictionReason.SelectionWithheld))]
    [DataRow(CaptureSatelliteProducts.PresentationWithheldPrefix + nameof(OrbitalPredictionReason.PropagationFailure))]
    [DataRow(CaptureSatelliteProducts.PresentationWithheldPrefix + nameof(OrbitalPredictionReason.InvalidGeometry))]
    [DataRow(CaptureSatelliteProducts.PresentationWithheldPrefix + nameof(OrbitalPredictionReason.MissingEarthOrientation))]
    [DataRow(CaptureSatelliteProducts.PresentationWithheldPrefix + nameof(OrbitalPredictionReason.UnsupportedLeapCrossing))]
    [DataRow(CaptureSatelliteProducts.PresentationWithheldPrefix + nameof(OrbitalPredictionReason.EvaluationBudget))]
    [DataRow(CaptureSatelliteProducts.PresentationWithheldPrefix + nameof(OrbitalPredictionReason.SampleBudget))]
    [DataRow(CaptureSatelliteProducts.PresentationWithheldPrefix + nameof(OrbitalPredictionReason.ResultBudget))]
    [DataRow(CaptureSatelliteProducts.PresentationWithheldPrefix + nameof(OrbitalPredictionReason.Deadline))]
    [DataRow(CaptureSatelliteProducts.PresentationWithheldPrefix + nameof(OrbitalPredictionReason.IncompleteInterval))]
    [DataRow(ExposureMeasurementSourceReasonCodes.LinearInputUnavailable)]
    [DataRow(ExposureMeasurementSourceReasonCodes.UnsupportedLinearLayout)]
    [DataRow(ExposureMeasurementSourceReasonCodes.IncompatibleLinearOrientation)]
    [DataRow(ExposureMeasurementSourceReasonCodes.UnqualifiedCfaPhase)]
    [DataRow(ExposureMeasurementSourceReasonCodes.CandidateBudgetExceeded)]
    [DataRow(ExposureMeasurementSourceReasonCodes.InsufficientBackground)]
    [DataRow(ExposureMeasurementSourceReasonCodes.AllCandidatesRejected)]
    [DataRow(ExposureMeasurementSourceReasonCodes.NoCandidates)]
    [DataRow(AstrometricSolverReasonCodes.CatalogIncomplete)]
    [DataRow(AstrometricSolverReasonCodes.CatalogSelectionUnsupported)]
    [DataRow(AstrometricSolverReasonCodes.CoordinateModelUnsupported)]
    [DataRow(AstrometricSolverReasonCodes.InsufficientDetections)]
    [DataRow(AstrometricSolverReasonCodes.WarmContextIncompatible)]
    [DataRow(AstrometricSolverReasonCodes.Accepted)]
    [DataRow(AstrometricSolverReasonCodes.Ambiguous)]
    [DataRow(AstrometricSolverReasonCodes.ResourceLimit)]
    [DataRow(AstrometricSolverReasonCodes.AcquisitionOrQualityFailed)]
    [DataRow(AstrometricSolverReasonCodes.TimeBudget)]
    [DataRow(MeasuredAstrometryAssessmentReasonCodes.ExecutionBudget)]
    [DataRow(MeasuredAstrometryAssessmentReasonCodes.MeasurementIncomplete)]
    [DataRow(MeasuredAstrometryAssessmentReasonCodes.MeasuredEntryBound)]
    [DataRow(MeasuredAstrometryAssessmentReasonCodes.CatalogCorrespondenceMismatch)]
    public void PublicationShowsExactLayerReasonAndWithholdsPaths(string code)
    {
        Assert.AreEqual(code, CenteredPublicationEvidence.Reason(code));
        Assert.AreEqual("Withheld", CenteredPublicationEvidence.Reason($"{code}/private"));
    }

    [TestMethod]
    [DataRow(Satellites, CenteredViewLayerScope.Contributor, nameof(CaptureSatelliteReason.CaptureLocationUnavailable))]
    [DataRow("measured-source-markers", CenteredViewLayerScope.Reference, ExposureMeasurementSourceReasonCodes.CandidateBudgetExceeded)]
    [DataRow(AstrometricDiagnosticPresentation.ResidualsLayer, CenteredViewLayerScope.Reference, AstrometricSolverReasonCodes.AcquisitionOrQualityFailed)]
    public void PublicationRendersLayerReasonInEvidenceCell(string kind, CenteredViewLayerScope scope, string code)
    {
        var reference = Exposures(false)[2];
        var layer = new CenteredViewLayer(new(kind, scope, reference.Capture.CaptureId), 1, Artifact(),
            reference.ExposureEndedUtc, CenteredViewReadiness.Incompatible, code, null);
        var view = View(reference, [reference], [layer]);
        using var context = new BunitContext();

        var cut = context.Render<CenteredPublicationEvidence>(parameters => parameters.Add(component => component.View, view));

        var row = cut.Find($"[data-layer-kind='{kind}'] tbody tr");
        Assert.AreEqual("Incompatible", row.Children[1].TextContent);
        Assert.Contains(code, row.Children[3].TextContent);
        Assert.DoesNotContain("Withheld", row.Children[3].TextContent);
    }

    [TestMethod]
    public void PublicationReasonsCoverDeclaredProducerCodes()
    {
        var layerCodes = ProducerReasonConstants(typeof(ExposureMeasurementSourceReasonCodes),
                typeof(AstrometricSolverReasonCodes), typeof(MeasuredAstrometryAssessmentReasonCodes))
            .Concat(Enum.GetNames<CaptureSatelliteReason>())
            .Concat(Enum.GetNames<OrbitalSnapshotUnavailableReason>())
            .Concat(Enum.GetNames<OrbitalPredictionReason>())
            .Concat(Enum.GetValues<OrbitalPredictionReason>().Select(static reason => $"PresentationWithheld:{reason}"))
            .Concat(Enum.GetValues<CameraAgentOrbitalTimeDataProblem>()
                .Where(static problem => problem != CameraAgentOrbitalTimeDataProblem.None)
                .Select(static problem => $"TimeInputsUnavailable:{problem}"));

        foreach (var code in layerCodes)
        {
            Assert.AreEqual(code, CenteredPublicationEvidence.Reason(code), $"Layer producer reason {code}");
            Assert.AreEqual("Withheld", CenteredPublicationEvidence.Reason($"{code}/private"), $"Path after {code}");
        }

        // Nightly keogram failures, calibration import and acquisition failures, and per-input
        // normalization exclusions belong to their own evidence, outside these Operations fields.
        var dottedCodes = ProducerReasonConstants(typeof(FrameWhiteThreshold), typeof(CloudAssessmentReasonCodes))
            .Concat(ProducerReasonConstants(typeof(ProcessingReasonCodes))
                .Where(static code => !code.Contains("keogram-", StringComparison.Ordinal)))
            .Concat(ProducerReasonConstants(typeof(CenteredRegisteredStackRecipe))
                .Where(static code => code.StartsWith("registration.", StringComparison.Ordinal)))
            .Concat(ProducerReasonConstants(typeof(CalibrationLibraryReasonCodes)).Where(static code => code is not (
                CalibrationLibraryReasonCodes.InvalidJson or CalibrationLibraryReasonCodes.PayloadTooLarge or
                CalibrationLibraryReasonCodes.UnsupportedSchema or CalibrationLibraryReasonCodes.InvalidBundle or
                CalibrationLibraryReasonCodes.Ambiguous or CalibrationLibraryReasonCodes.PublicationConflict or
                CalibrationLibraryReasonCodes.AcquisitionFailure or CalibrationLibraryReasonCodes.MasterBuildFailure)))
            .Concat(ProducerReasonConstants(typeof(CalibrationFamilyReasonCodes)).Where(static code =>
                code.StartsWith("calibration.family.master-", StringComparison.Ordinal) ||
                code is CalibrationFamilyReasonCodes.InvalidFamily or CalibrationFamilyReasonCodes.IdentityMismatch))
            .Concat(ProducerReasonConstants(typeof(GainNormalizationReasonCodes)).Where(static code => code is not (
                GainNormalizationReasonCodes.GainOutOfRange or GainNormalizationReasonCodes.MixedResponse or
                GainNormalizationReasonCodes.MixedConversionRegime or GainNormalizationReasonCodes.CoefficientRatioExceeded or
                GainNormalizationReasonCodes.InvalidContract or GainNormalizationReasonCodes.UnsupportedSchema or
                GainNormalizationReasonCodes.NonCanonicalJson)))
            .Concat([
                ProcessingPlanSupersededException.ExecutionReason,
                ActiveCalibrationFamilySelector.NoActiveFamily,
                NormalizationPlanFreezer.NoEligibleInput,
                NormalizationPlanFreezer.NonRightAlignedResponse,
                NormalizationPlanFreezer.WindowIncomplete,
                CalibrationFamilyActivationReasonCodes.IdentityMismatch,
                CalibrationFamilyActivationReasonCodes.AcquisitionModelMismatch
            ]);

        foreach (var code in dottedCodes)
            Assert.AreEqual(code, CenteredPublicationEvidence.Reason(code), $"Structured producer reason {code}");
    }

    private static IEnumerable<string> ProducerReasonConstants(params Type[] types) =>
        types.SelectMany(static type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!);

    [TestMethod]
    public void LiveReadinessDistinguishesMissingProgressDisabledAndAssessedEmpty()
    {
        var captures = Exposures(false);
        var sources = captures[..5];
        var satellite = sources.Select((source, i) => Layer(source, i == 4 ? CenteredViewReadiness.Waiting : CenteredViewReadiness.AssessedEmpty));
        var disabled = sources.Select(source => Layer(source, CenteredViewReadiness.NotConfigured, "disabled-layer"));
        var view = View(sources[2], sources, [.. satellite, .. disabled]);

        var live = CenteredPublicationEvidence.Layers(view, live: true);
        var waitingAtPublication = live.Single(group => group.Kind == Satellites);
        Assert.AreEqual("At least 4/5 assessed · 1 unavailable", waitingAtPublication.Assessment);
        Assert.AreEqual(CenteredViewReadiness.AssessedEmpty, waitingAtPublication.Sites[0].Readiness);
        Assert.IsNull(waitingAtPublication.Sites[4].Readiness);
        Assert.AreEqual("Waiting at publication. Current producer state unavailable.", waitingAtPublication.Sites[4].Description);
        var off = live.Single(group => group.Kind == "disabled-layer");
        Assert.IsNull(off.Assessment);
        Assert.IsTrue(off.Sites.All(site => site.Readiness == CenteredViewReadiness.NotConfigured));

        var earlier = view with { Revision = view.Revision with { Layers = [Layer(sources[0], CenteredViewReadiness.Waiting)] } };
        var unknown = Assert.ContainsSingle(CenteredPublicationEvidence.Layers(earlier, live: true));
        Assert.AreEqual("Assessment unavailable · 5/5 source states unavailable", unknown.Assessment);
        Assert.AreEqual(CenteredViewReadiness.Waiting, CenteredPublicationEvidence.Layers(earlier)[0].Sites[0].Readiness);
    }

    [TestMethod]
    public void RestartKeepsTheIssuedRevisionAndShowsOnlyRecordedAcquisition()
    {
        var captures = Exposures(false);
        var view = View(captures[2], captures[..5], []);
        var observation = Runtime(captures[4], CenteredRuntimeEvidenceBasis.RetainedJournal) with
        {
            RunId = Guid.NewGuid(),
            AcquisitionHead = null,
            GraphRevisionId = new('B', 64),
            Holds = null
        };
        using var context = new BunitContext();
        var snapshot = new CenteredPipelineRunningSnapshot(Stream.Camera, Paused: true, Acquisition: observation, Display: Current(view));
        var cut = context.Render<CenteredRunningEvidence>(parameters => parameters.Add(component => component.Snapshot, snapshot));
        Assert.Contains("Acquisition paused · retained journal", cut.Markup);
        Assert.Contains("Unavailable / 8", cut.Markup);
        Assert.Contains("different acquisition run or generation", cut.Markup);
        Assert.Contains("Capture #103", cut.Markup);
        Assert.Contains("Layer readiness evidence unavailable", cut.Markup);

        cut.Render(parameters => parameters.Add(component => component.Snapshot, new CenteredPipelineRunningSnapshot(Stream.Camera)));
        Assert.Contains("Unavailable / Unavailable", cut.Markup);
        Assert.Contains("No issued display revision is available", cut.Markup);
        Assert.DoesNotContain("Capture #103", cut.Markup);
    }

    [TestMethod]
    public void WindowShowsBothUnsettledStartupAndFrozenExclusions()
    {
        var captures = Exposures(false);
        var reference = captures[2];
        var startup = new CenteredWindowSnapshot(reference.Capture.CaptureId, Checksum, Stream.Camera, Stream.RunId,
            Start.AddSeconds(15), null, null, false, null, null, null, null, [],
            [.. captures.Take(5).Select((source, index) => new CenteredWindowSlotSnapshot(index - 2, source.ExposureStartedUtc,
                null, index == 2 ? reference.Input.Key : null, index == 0 ? CenteredWindowSlotState.OutsideRun : null, null))]);
        using var context = new BunitContext();
        var snapshot = new CenteredPipelineRunningSnapshot(Stream.Camera, Acquisition: Runtime(reference, CenteredRuntimeEvidenceBasis.ActiveAcquisition),
            Head: reference.Input, Window: startup);
        var cut = context.Render<CenteredRunningEvidence>(parameters => parameters.Add(component => component.Snapshot, snapshot));
        var window = cut.Find("[aria-label='Centered window']");
        Assert.Contains("Acquisition head window · Open · expected slot times", window.TextContent);
        Assert.AreEqual(5, window.QuerySelectorAll("tbody tr").Length);
        Assert.Contains("Unsettled", window.TextContent);
        Assert.Contains("Outside Run", window.TextContent);

        var failed = startup with
        {
            IsTerminal = true,
            EvidenceCutoffUtc = Start.AddSeconds(15),
            FailureReason = CenteredRegisteredStackRecipe.ResourceLimit
        };
        cut.Render(parameters => parameters.Add(component => component.Snapshot, snapshot with { Window = failed }));
        var failedWindow = cut.Find("[aria-label='Centered window']");
        Assert.Contains("Registration failed", failedWindow.TextContent);
        Assert.Contains(CenteredRegisteredStackRecipe.ResourceLimit, failedWindow.TextContent);

        var partial = View(reference, [captures[0], reference], [], captures[..5]);
        cut.Render(parameters => parameters.Add(component => component.Snapshot, snapshot with { Display = Current(partial), Window = null }));
        Assert.Contains("2/5 window slots contributed", cut.Markup);
        Assert.Contains("Registration Rejected", cut.Find("[aria-label='Centered window']").TextContent);
        Assert.Contains("Missing Exposure", cut.Find("[aria-label='Centered window']").TextContent);
    }

    [TestMethod]
    public async Task ArchiveLinksSelectOriginalAndLateEnrichmentOfTheExactReferenceExecution()
    {
        var captures = Exposures(false);
        var reference = captures[2];
        var original = View(reference, captures[..5], []);
        var latest = original with { Revision = original.Revision with { RevisionId = new('D', 64), Layers = [Layer(captures[4], CenteredViewReadiness.Ready)] } };
        var successor = View(captures[3], captures[1..], [Layer(captures[4], CenteredViewReadiness.Ready)]);
        var index = new Mock<ICenteredViewReadIndex>(MockBehavior.Strict);
        var lookup = new Mock<ICenteredViewLookup>(MockBehavior.Strict);
        var token = TestContext.CancellationToken;
        lookup.Setup(reader => reader.ReadArchiveAsync(reference.Capture.CaptureId, token)).ReturnsAsync([
            new(original.Base, original.Revision.RevisionId, latest.Revision.RevisionId, original.Revision.RevisionId),
            new(successor.Base, successor.Revision.RevisionId, successor.Revision.RevisionId, successor.Revision.RevisionId)]);
        index.Setup(reader => reader.ReadRevisionAsync(original.Base.BaseId, original.Revision.RevisionId, token)).ReturnsAsync(original);
        index.Setup(reader => reader.ReadRevisionAsync(original.Base.BaseId, latest.Revision.RevisionId, token)).ReturnsAsync(latest);
        var execution = Execution(reference);
        var read = await CenteredPipelinePublications.ReadRunAsync(index.Object, lookup.Object, execution, NullLogger.Instance, token).ConfigureAwait(false);
        Assert.HasCount(2, read);
        Assert.AreSame(original, read[0].View);
        Assert.AreSame(latest, read[1].View);
        Assert.IsEmpty(await CenteredPipelinePublications.ReadRunAsync(index.Object, lookup.Object,
            execution with { PrimaryArtifactId = Guid.NewGuid() }, NullLogger.Instance, token).ConfigureAwait(false));

        index.Setup(reader => reader.ReadRevisionAsync(original.Base.BaseId, latest.Revision.RevisionId, token)).ThrowsAsync(new IOException("incomplete"));
        read = await CenteredPipelinePublications.ReadRunAsync(index.Object, lookup.Object, execution, NullLogger.Instance, token).ConfigureAwait(false);
        Assert.AreSame(original, read[0].View);
        Assert.AreEqual(latest.Revision.RevisionId, read[1].RevisionId);
        Assert.IsNull(read[1].View);
    }

    [TestMethod]
    public void RawFallbackDoesNotReplaceMissingLinearEvidenceOrReportAnInvalidLatency()
    {
        var reference = Exposures(false)[2] with { Linear = null };
        var view = View(reference, [reference], []);
        view = view with
        {
            Base = view.Base with
            {
                Definition = view.Base.Definition with
                {
                    Kind = CenteredViewKind.Single,
                    ClosedWindow = null,
                    Window = null,
                    Preview = view.Base.Definition.Preview with
                    {
                        Source = CenteredViewImageSource.RawFallback,
                        QualificationReason = FrameWhiteThreshold.OpaqueAdcReason
                    },
                    Contributors = [new(reference, nameof(CenteredRegistrationMethod.ReferenceIdentity), default)]
                }
            },
            Revision = view.Revision with { PublishedUtc = reference.ExposureStartedUtc }
        };
        using var context = new BunitContext();
        var cut = context.Render<CenteredPublicationEvidence>(parameters => parameters.Add(component => component.View, view));
        Assert.Contains("Raw Fallback", cut.Markup);
        Assert.Contains(FrameWhiteThreshold.OpaqueAdcReason, cut.Find(".publication-facts").TextContent);
        Assert.Contains("Linear product unavailable", cut.Markup);
        Assert.DoesNotContain("Source to reference mapping", cut.Markup);
        Assert.IsNull(CenteredPipelinePublications.ExposureEndToPublication(view));
    }

    [TestMethod]
    public void WaitingProofRejectsSupersededCancelledOrUnrelatedExecutions()
    {
        var source = Exposures(true)[4];
        var execution = Execution(source);
        var run = new ProcessingGraphExecutionDetail(execution, []);
        Assert.IsTrue(CenteredViewLayerProgressReader.IsActiveSource(run, Stream, source));
        Assert.IsFalse(CenteredViewLayerProgressReader.IsActiveSource(run, Stream with { RunId = Guid.NewGuid() }, source));
        foreach (var mismatched in new[]
        {
            execution with { GraphRevisionId = new('B', 64) }, execution with { PrimaryArtifactId = Guid.NewGuid() },
            execution with { CaptureId = Guid.NewGuid() }, execution with { ExecutionId = Guid.NewGuid() },
            execution with { CancellationRequested = true }, execution with { Status = ProcessingGraphExecutionStatus.Completed },
            execution with { ExecutionClass = ProcessingGraphExecutionClass.Replay }
        })
            Assert.IsFalse(CenteredViewLayerProgressReader.IsActiveSource(run with { Execution = mismatched }, Stream, source));
        Assert.IsFalse(CenteredViewLayerProgressReader.IsActiveSource(null, Stream, source));
    }

    [TestMethod]
    [DataRow("Pending", true)]
    [DataRow("Running", true)]
    [DataRow("RetryableFailure", true)]
    [DataRow("TerminalFailure", false)]
    public async Task LayerProgressWaitsForAProducerThatCanStillRun(string status, bool pending)
    {
        const string producer = "satellite-layer";
        var source = Exposures(false)[2];
        var layer = Layer(source, CenteredViewReadiness.Waiting);
        var view = View(source, [source], [layer]);
        var output = new ProcessingGraphProductContract(FrameArtifactRole.Metadata, "capture-satellites-v1-layer",
            ProcessingProductKind.Metadata, SchemaVersion: PresentationLayerPayloadV1.CurrentSchemaVersion);
        var graph = new ProcessingGraphDefinition(ProcessingGraphSchemaVersions.V1, "centered", "1", [],
            [new(producer, CaptureSatelliteLayerProcessingStep.Alias, "1", ProcessingOperationKind.Transform, true,
                ProcessingGraphNodeFailurePolicy.Optional, 0, JsonSerializer.SerializeToElement(new { }), [], [], [output], null, [], [])]);
        var pipeline = CapturePipelineConfig.Empty;
        var pipelineJson = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.SerializeToElement(pipeline));
        var revision = new ProcessingGraphRevisionSnapshot(
            new(Stream.GenerationId, graph.Name, graph.Revision, ProcessingGraphRevisionLifecycle.Validated,
                ProcessingGraphJson.ComputeDefinitionIdentity(graph), Checksum, Checksum, Start, Start, null, null),
            pipeline, pipelineJson, ProcessingGraphJson.SerializeCanonical(graph), "{}"u8.ToArray(), []);
        var token = TestContext.CancellationToken;
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-layer-progress");
        try
        {
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            await journal.InitializeAsync(token).ConfigureAwait(false);
            using var store = new SqliteCaptureProcessingStore(Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }));
            await store.UpsertConfiguredBasicRevisionAsync(revision, token).ConfigureAwait(false);
            var operations = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
            var node = new ProcessingGraphExecutionNodeState(producer, false, Checksum, status, null, 1, Start, null, [], [], [])
            {
                OutputContracts = [output]
            };
            operations.Setup(service => service.ReadRunManifestAsync(source.ExecutionId, token))
                .ReturnsAsync(new ProcessingGraphExecutionDetail(Execution(source), [node]));

            var progress = await new CenteredViewLayerProgressReader(store, operations.Object).ReadPendingAsync(view, token).ConfigureAwait(false);

            if (pending)
                Assert.AreEqual(layer.Key, Assert.ContainsSingle(progress));
            else
                Assert.IsEmpty(progress);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public TestContext TestContext { get; set; } = default!;

    private static CenteredViewExposure[] Exposures(bool bayer) => Enumerable.Range(101, 6).Select(sequence => Exposure(sequence, bayer)).ToArray();

    private static ProcessingGraphExecutionState Execution(CenteredViewExposure source) => new(source.ExecutionId,
        ProcessingGraphExecutionClass.Live, ProcessingGraphExecutionStatus.Running, source.Capture.CaptureId,
        source.Input.Raw.Artifact.ArtifactId, Stream.GenerationId, Checksum, Checksum, Checksum, "live", null, 0,
        Start, Start, Start.AddMinutes(1), Start.AddMinutes(2), Start, null, null, false, 1);

    private static CenteredExposureRuntimeSnapshot Runtime(CenteredViewExposure source, CenteredRuntimeEvidenceBasis basis) =>
        new(Stream.Camera, Stream.RunId, Stream.GenerationId, CenteredExposureProfile.CurrentExecutionVersion,
            new(source.Input.Key, source.Capture.CaptureSequence - 100, source.ExposureStartedUtc, source.Input.Raw.Timing.DurableIngressUtc),
            5, 8, new(0, 2, 2048), Start.AddSeconds(30), basis, null);

    private static CenteredViewCurrent Current(CenteredView view) => new(view,
        new(view.Base.BaseId, view.Revision.RevisionId, 11, Start.AddSeconds(20), null, null));
    private static CenteredViewExposure Exposure(long sequence, bool bayer)
    {
        var capture = new CaptureIdentityDescriptor(Stream.Camera.AgentId, "test-rig", sequence, Guid.NewGuid());
        var start = Start.AddSeconds(2 * (sequence - 100));
        var timing = new CaptureTimingDescriptor(start, start, start.AddSeconds(1), start.AddSeconds(1.25), start.AddSeconds(1.5));
        var exposure = TimeSpan.FromSeconds(1);
        var controls = new CaptureControlDescriptor(exposure, exposure, 1, 1, null, null, null, null);
        var profile = new ProfileIdentityDescriptor("fixture", "1", Checksum);
        var layout = new FrameLayoutDescriptor(32, 24, 64, bayer ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned, bayer ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            0, ushort.MaxValue, 1536)
        {
            Readout = new(34, 26, 1, 1, 32, 24, 1, 1, FrameBinningAlgorithm.IdentityV1, bayer ? 1 : null, bayer ? 1 : null)
        };
        var raw = new ReconstructionDescriptor(capture, timing, controls, new(profile, profile, profile, profile, profile), layout,
            new(Guid.NewGuid(), FrameArtifactRole.Raw, "fixture", "raw", timing.DurableIngressUtc, [], Recipe(), "application/octet-stream", Checksum));
        var input = new ExposureInputV1(new(Stream.Camera, Stream.RunId, capture.CaptureId), raw, new(layout, new(90, 0, 0), Checksum, "1", null));
        return new(input, Guid.NewGuid(), Artifact());
    }

    private static CenteredView View(CenteredViewExposure reference, CenteredViewExposure[] contributors,
        IReadOnlyList<CenteredViewLayer> layers, CenteredViewExposure[]? slots = null)
    {
        var product = new PresentationProductReference(Guid.NewGuid(), Checksum, "image/jpeg", new(32, 24, Checksum, Checksum));
        var preview = new CenteredViewPreview(product, Checksum, Recipe(), "jpeg", "fixture", new(32, 24, 0, 0, 32, 24, 32, 24));
        var definition = new CenteredViewBaseDefinition(Stream, reference, reference.Capture.CaptureSequence, CenteredViewKind.Stack,
            Artifact(), preview, contributors.Select(source => new CenteredViewContributor(source,
                source == reference ? nameof(CenteredRegistrationMethod.ReferenceIdentity) : nameof(CenteredRegistrationMethod.VerifiedCentroidSimilarity),
                JsonSerializer.SerializeToElement(new { offsetX = source.Capture.CaptureSequence - reference.Capture.CaptureSequence }))).ToArray(), Artifact())
        {
            Window = new(Checksum, Checksum, Checksum)
            {
                DeadlineUtc = Start.AddSeconds(16),
                EvidenceCutoffUtc = Start.AddSeconds(15),
                Slots = (slots ?? contributors).Select((source, i) => new CenteredViewWindowSlot(i - 2, source.ExposureStartedUtc,
                    i == 3 && slots is not null ? null : source.Capture.CaptureId, source.ExposureStartedUtc, source.ExposureEndedUtc,
                    i == 3 && slots is not null ? "MissingExposure" : "Eligible",
                    contributors.Contains(source) ? "Contributed" : i == 3 ? "MissingSlot" : "RegistrationRejected", null)).ToArray()
            }
        };
        var baseId = reference.Capture.CaptureSequence == 103 ? new string('A', 64) : new string('B', 64);
        var published = reference.ExposureEndedUtc.AddSeconds(9);
        return new(new(baseId, definition, published), new(new('C', 64), baseId, 10, published,
            new(OverlayManifestV1.CurrentSchemaVersion, Checksum, product, null, []), layers));
    }

    private static CenteredViewLayer Layer(CenteredViewExposure source, CenteredViewReadiness readiness, string kind = Satellites) =>
        new(new(kind, CenteredViewLayerScope.Contributor, source.Capture.CaptureId), 1,
            readiness is CenteredViewReadiness.Ready or CenteredViewReadiness.AssessedEmpty ? Artifact() : null,
            source.ExposureEndedUtc, readiness, null, null);

    private static CenteredViewArtifact Artifact() => new(Guid.NewGuid(), Checksum, Checksum);
    private static RecipeIdentityDescriptor Recipe() => RecipeIdentityDescriptor.Create("fixture", "1.0.0", "test", JsonSerializer.SerializeToElement(new { }));
}
