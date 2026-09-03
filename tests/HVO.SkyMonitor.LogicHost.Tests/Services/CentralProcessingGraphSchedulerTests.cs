using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralProcessingGraphSchedulerTests
{
    [TestMethod]
    public void CanonicalBasicGraphHasExecutableExplicitTopology()
    {
        var catalog = new CentralDerivativeRecipeCatalog(new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            SourceRole = FrameArtifactRole.Raw
        });
        var registry = new CentralProcessingGraphNodeRegistry(catalog);
        var definition = DatabaseSeeder.CreateBasicCentralProcessingGraph();

        var compiled = ProcessingGraphCompiler.Compile(
            definition, new(ProcessingGraphHosts.LogicHost, registry.Capabilities));

        Assert.IsTrue(compiled.IsValid);
        var plan = compiled.Plan!;
        Assert.IsTrue(registry.Validate(plan));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "Preview", "Annotation", "ImageQuality", "CloudAssessment", "RollingMean",
                "WeatherCloudOverlay"
            },
            plan.Nodes.Select(node => node.Definition.Id).ToArray());
        var rolling = plan.Nodes.Single(node => node.Definition.Id == "RollingMean").Definition;
        Assert.AreEqual(ProcessingGraphWindowKind.Centered, rolling.Window!.Kind);
        CollectionAssert.AreEqual(new[] { -2, -1, 0, 1, 2 }, rolling.Window.RequiredPositions.ToArray());
        var overlay = plan.Nodes.Single(node => node.Definition.Id == "WeatherCloudOverlay").Definition;
        CollectionAssert.AreEquivalent(
            new[] { "Preview", "CloudAssessment" },
            overlay.Dependencies.Select(dependency => dependency.ProducerId).ToArray());
        Assert.IsFalse(plan.Nodes.Any(node => node.Definition.Id == "TransientDetection"));
    }

    [TestMethod]
    public void CentralRegistryRejectsPlansThatCannotBeFaithfullyMaterialized()
    {
        var registry = new CentralProcessingGraphNodeRegistry(new CentralDerivativeRecipeCatalog());
        var baseline = CreatePreviewGraph();
        var source = baseline.Sources[0];
        var node = baseline.Nodes[0];
        var multipleSourceOutputs = baseline with
        {
            Sources = [source with
            {
                Outputs = [.. source.Outputs, new ProcessingGraphProductContract(
                    FrameArtifactRole.Calibrated, "calibrated", ProcessingProductKind.PixelData)]
            }]
        };
        var annotationBinding = baseline with
        {
            Nodes = [new ProcessingGraphNodeDefinition(
                node.Id, node.StepAlias, node.StepVersion, node.OperationKind, node.Enabled,
                node.FailurePolicy, node.Order, node.EffectiveOptions,
                [new ProcessingGraphDependencyDefinition("$raw", ProcessingGraphDependencyKind.Annotation)],
                [node.Inputs[0] with { BindingKind = ProcessingGraphInputBindingKind.Annotation }],
                node.Outputs, node.Window, node.CapabilityLabels, node.HostApplicability)]
        };
        var canonicalJsonBinding = WithCanonicalJsonBinding(baseline);
        var noPrimaryBinding = baseline with
        {
            Nodes = [new ProcessingGraphNodeDefinition(
                node.Id, node.StepAlias, node.StepVersion, node.OperationKind, node.Enabled,
                node.FailurePolicy, node.Order, node.EffectiveOptions, node.Dependencies,
                [node.Inputs[0] with { BindingKind = ProcessingGraphInputBindingKind.AuxiliaryArtifact }],
                node.Outputs, node.Window, node.CapabilityLabels, node.HostApplicability)]
        };

        Assert.IsTrue(registry.Validate(ProcessingGraphCompiler.Compile(
            baseline, new(ProcessingGraphHosts.LogicHost, registry.Capabilities)).Plan!));
        foreach (var definition in new[] { multipleSourceOutputs, annotationBinding, canonicalJsonBinding, noPrimaryBinding })
        {
            var compiled = ProcessingGraphCompiler.Compile(
                definition, new(ProcessingGraphHosts.LogicHost, registry.Capabilities));
            Assert.IsTrue(compiled.IsValid, string.Join(Environment.NewLine, compiled.Diagnostics));
            Assert.IsFalse(registry.Validate(compiled.Plan!));
        }
    }

    [TestMethod]
    public void CentralRegistryRejectsAuxiliaryArtifactBindingsOnAnnotationNodes()
    {
        // ResolveLeaseSceneProvenance treats Expected == Requested as "no annotation frozen"; that marker is only
        // unambiguous while annotation nodes carry a primary binding alone, and the recipe ignores auxiliaries anyway.
        var registry = new CentralProcessingGraphNodeRegistry(new CentralDerivativeRecipeCatalog());
        var baseline = CreateAnnotationConsumerGraph();
        var calibratedSource = new ProcessingGraphSourceDefinition(
            "$calibrated",
            [new ProcessingGraphProductContract(FrameArtifactRole.Calibrated, "source", ProcessingProductKind.PixelData)]);
        var auxiliary = new ProcessingGraphInputContract(
            [FrameArtifactRole.Calibrated], [ProcessingProductKind.PixelData], [], [], [],
            BindingName: "reference", BindingKind: ProcessingGraphInputBindingKind.AuxiliaryArtifact);
        ProcessingGraphDefinition WithAuxiliaryOn(string nodeId) => baseline with
        {
            Sources = [.. baseline.Sources, calibratedSource],
            Nodes = [.. baseline.Nodes.Select(node => node.Id != nodeId
                ? node
                : new ProcessingGraphNodeDefinition(
                    node.Id, node.StepAlias, node.StepVersion, node.OperationKind, node.Enabled,
                    node.FailurePolicy, node.Order, node.EffectiveOptions,
                    [.. node.Dependencies, new ProcessingGraphDependencyDefinition("$calibrated")],
                    [.. node.Inputs, auxiliary],
                    node.Outputs, node.Window, node.CapabilityLabels, node.HostApplicability))]
        };

        Assert.IsTrue(registry.Validate(ProcessingGraphCompiler.Compile(
            baseline, new(ProcessingGraphHosts.LogicHost, registry.Capabilities)).Plan!));
        var previewAuxiliary = ProcessingGraphCompiler.Compile(
            WithAuxiliaryOn("Preview"), new(ProcessingGraphHosts.LogicHost, registry.Capabilities));
        Assert.IsTrue(previewAuxiliary.IsValid, string.Join(Environment.NewLine, previewAuxiliary.Diagnostics));
        Assert.IsTrue(registry.Validate(previewAuxiliary.Plan!),
            "auxiliary artifact bindings remain host-compatible on non-annotation nodes");
        var annotationAuxiliary = ProcessingGraphCompiler.Compile(
            WithAuxiliaryOn("Annotation"), new(ProcessingGraphHosts.LogicHost, registry.Capabilities));
        Assert.IsTrue(annotationAuxiliary.IsValid, string.Join(Environment.NewLine, annotationAuxiliary.Diagnostics));
        Assert.IsFalse(registry.Validate(annotationAuxiliary.Plan!));
    }

    [TestMethod]
    public async Task CatalogRejectsCentralPublicationOfCanonicalJsonBoundGraphs()
    {
        await using var context = CreateContext();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var registry = new CentralProcessingGraphNodeRegistry(new CentralDerivativeRecipeCatalog());
        var catalog = new ProcessingGraphCatalogService(
            context, registry, TimeProvider.System, catalogTelemetry, NullLogger<ProcessingGraphCatalogService>.Instance);
        var definition = WithCanonicalJsonBinding(CreatePreviewGraph()) with { Name = "canonical-json-central" };
        var portable = ProcessingGraphCompiler.Compile(definition);
        Assert.IsTrue(portable.IsValid, string.Join(Environment.NewLine, portable.Diagnostics));

        var created = await catalog.CreateRevisionAsync(
            definition, "operator", isPlatformEditor: true, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CentralProcessingGraphMutationOutcome.Invalid, created.Outcome);
        Assert.AreEqual("host-incompatible", created.ReasonCode);
        Assert.AreEqual(0, await context.CentralProcessingGraphRevisions.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public void FrozenGraphLeaseRequiresRecomputedPlanAndNodeIdentity()
    {
        var plan = ProcessingGraphCompiler.Compile(
            CreatePreviewGraph(), new(ProcessingGraphHosts.LogicHost, [])).Plan!;
        var lease = CreateGraphLease(plan);

        Assert.IsTrue(CentralDerivativeJobExecutor.HasValidFrozenGraphPlan(lease));
        Assert.IsFalse(CentralDerivativeJobExecutor.HasValidFrozenGraphPlan(lease with
        {
            FrozenNodePlanJson = lease.FrozenNodePlanJson!.Replace(
                CentralDerivativeRecipeCatalog.PreviewVariant, "tampered-preview", StringComparison.Ordinal)
        }));
        Assert.IsFalse(CentralDerivativeJobExecutor.HasValidFrozenGraphPlan(lease with
        {
            CentralPlanIdentitySha256 = new string('0', 64)
        }));
        Assert.IsFalse(CentralDerivativeJobExecutor.HasValidFrozenGraphPlan(lease with
        {
            GraphNodeOrdinal = 1
        }));
        Assert.IsFalse(CentralDerivativeJobExecutor.HasValidFrozenGraphPlan(lease with
        {
            FrozenCentralPlanJson = lease.FrozenCentralPlanJson![..^1] + ",\"unknown\":true}"
        }));
        Assert.IsFalse(CentralDerivativeJobExecutor.HasValidFrozenGraphPlan(lease with
        {
            FrozenCentralPlanJson = "{"
        }));
    }

    [TestMethod]
    public void SchedulerHelpersPreserveWindowEnvironmentAndCycleSemantics()
    {
        CollectionAssert.AreEqual(
            new[] { -2, -1, 0, 1, 2 },
            CentralProcessingGraphScheduler.CreateWindowOffsets(new ProcessingGraphWindowRequirement(
                ProcessingGraphWindowKind.Centered,
                5,
                5,
                [-2, -1, 0, 1, 2],
                ["rig"])).ToArray());
        CollectionAssert.AreEqual(
            new[] { -4, -3, -2, -1, 0 },
            CentralProcessingGraphScheduler.CreateWindowOffsets(new ProcessingGraphWindowRequirement(
                ProcessingGraphWindowKind.Trailing,
                5,
                5,
                [-4, -3, -2, -1, 0],
                ["rig"])).ToArray());
        Assert.AreEqual(CentralDerivativeWindowOutcome.Run,
            CentralProcessingGraphScheduler.MapMissingOutcome(ProcessingGraphMissingInputOutcome.Run));
        Assert.AreEqual(CentralDerivativeWindowOutcome.Skip,
            CentralProcessingGraphScheduler.MapMissingOutcome(ProcessingGraphMissingInputOutcome.Skip));
        Assert.AreEqual(CentralDerivativeWindowOutcome.Fail,
            CentralProcessingGraphScheduler.MapMissingOutcome(ProcessingGraphMissingInputOutcome.Fail));
        Assert.AreEqual(CentralDerivativeWindowOutcome.Quarantine,
            CentralProcessingGraphScheduler.MapMissingOutcome(ProcessingGraphMissingInputOutcome.Quarantine));
        Assert.IsNull(CentralProcessingGraphScheduler.MapMissingOutcome(null));
        Assert.ThrowsExactly<CentralDerivativeJobStateException>(() =>
            CentralProcessingGraphScheduler.MapMissingOutcome((ProcessingGraphMissingInputOutcome)int.MaxValue));
        Assert.AreEqual(11, CentralProcessingGraphScheduler.AddSequenceOffset(10, 1));
        Assert.IsNull(CentralProcessingGraphScheduler.AddSequenceOffset(null, 1));
        Assert.IsNull(CentralProcessingGraphScheduler.AddSequenceOffset(long.MaxValue, 1));

        Assert.IsFalse(CentralProcessingGraphScheduler.IsPrecipitationDetected(null));
        Assert.IsTrue(CentralProcessingGraphScheduler.IsPrecipitationDetected(
            CreateObservation(EnvironmentalObservationKind.RainState, booleanValue: true)));
        Assert.IsFalse(CentralProcessingGraphScheduler.IsPrecipitationDetected(
            CreateObservation(EnvironmentalObservationKind.RainState, booleanValue: false)));
        Assert.IsTrue(CentralProcessingGraphScheduler.IsPrecipitationDetected(
            CreateObservation(EnvironmentalObservationKind.PrecipitationRate, numericValue: 0.1)));
        Assert.IsFalse(CentralProcessingGraphScheduler.IsPrecipitationDetected(
            CreateObservation(EnvironmentalObservationKind.PrecipitationRate, numericValue: 0)));
        Assert.IsFalse(CentralProcessingGraphScheduler.IsPrecipitationDetected(
            CreateObservation(EnvironmentalObservationKind.AirTemperature, numericValue: 5)));

        var cycle = new CaptureCycleEvidence(
            CaptureCadenceMode.MinimumStartInterval,
            CaptureStartReason.Initial,
            AutomaticControlOwnership.Disabled,
            AutomaticControlOwnership.Disabled,
            CaptureSolarRegime.Night,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            new CaptureControlDecisionEvidence(
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                TimeSpan.FromSeconds(1),
                0,
                TimeSpan.FromSeconds(1),
                0,
                CaptureControlDecisionReason.Disabled),
            DateTimeOffset.UnixEpoch)
        {
            MonotonicStartJitter = TimeSpan.Zero
        };
        var canonicalCycleJson = CaptureContractJson.SerializeToElement(cycle).GetRawText();
        Assert.AreEqual(
            CaptureSolarRegime.Night,
            CentralProcessingGraphScheduler.ParseSolarRegime(canonicalCycleJson));
        Assert.IsNull(CentralProcessingGraphScheduler.ParseSolarRegime(null));
        Assert.IsNull(CentralProcessingGraphScheduler.ParseSolarRegime(" "));
    }

    [TestMethod]
    public async Task ScheduleReplayExpandsSealsAndEnforcesIdempotencyConflict()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var source = CreateArtifact();
        var observatoryId = Guid.NewGuid();
        var camera = new LogicalCamera
        {
            ObservatoryId = observatoryId,
            Slug = "camera",
            Name = "Camera",
            Description = "Test",
            CreatedAtUtc = now.AddDays(-1),
            CreatedByUserId = "operator"
        };
        var installation = new LogicalCameraInstallation
        {
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            RegistrationId = Guid.NewGuid(),
            InstallationPublicId = Guid.NewGuid(),
            AssignedAtUtc = now.AddDays(-1),
            AssignedByUserId = "operator",
            AssignmentReasonCode = "test"
        };
        camera.Installations.Add(installation);
        source.Frame!.ObservatoryId = observatoryId;
        source.Frame.RegistrationId = installation.RegistrationId;
        source.Frame.LogicalCameraInstallation = installation;
        source.Frame.LogicalCameraInstallationId = installation.Id;
        var definition = CreatePreviewGraph(withWindow: true);
        var portable = ProcessingGraphCompiler.Compile(definition).Plan!;
        var central = ProcessingGraphCompiler.Compile(
            definition, new(ProcessingGraphHosts.LogicHost, [])).Plan!;
        var revision = new CentralProcessingGraphRevision
        {
            Name = definition.Name,
            Revision = definition.Revision,
            DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)),
            DefinitionIdentitySha256 = portable.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = portable.PlanIdentitySha256,
            CentralPlanIdentitySha256 = central.PlanIdentitySha256,
            CreatedAtUtc = now.AddMinutes(-1),
            CreatedByUserId = "operator",
            PublishedAtUtc = now.AddSeconds(-1),
            PublishedByUserId = "operator"
        };
        var assignment = new CentralProcessingGraphAssignment
        {
            Revision = revision,
            RevisionId = revision.Id,
            TargetHost = CentralProcessingGraphTargetHost.Central,
            Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
            ObservatoryId = observatoryId,
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now.AddMinutes(-1),
            ActorUserId = "operator",
            ReasonCode = "test"
        };
        revision.Assignments.Add(assignment);
        source.Frame!.Artifacts.Add(source);
        var calibrated = new CentralArtifact
        {
            CentralFrameId = source.Frame.Id,
            Frame = source.Frame,
            DevicePublicId = source.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Calibrated,
            Variant = "calibrated",
            RecipeVersion = "calibrated-v1",
            ManifestSchemaVersion = "manifest-v1",
            MediaType = "application/x-hvo-linear-frame",
            ByteLength = 2,
            ChecksumSha256 = new string('2', 64),
            StorageReference = $"s3://skymonitor-artifacts/{Guid.NewGuid():N}",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReceivedAtUtc = now,
            CreatedUtc = now,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        source.Frame.Artifacts.Add(calibrated);
        context.AddRange(camera, installation, source.Frame, source, calibrated, revision, assignment);
        await context.SaveChangesAsync().ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var windowResolver = new RecordingWindowResolver();
        var scheduler = CreateScheduler(
            context,
            telemetry,
            catalogTelemetry,
            windowResolver,
            new WindowNodeRegistry(new CentralDerivativeRecipeCatalog()));
        var request = new CentralProcessingGraphReplayRequest(
            revision.Id, [source.Id], "operator", "replay-key", "test-replay");

        var created = await scheduler.ScheduleReplayAsync(request, now, CancellationToken.None).ConfigureAwait(false);
        var existing = await scheduler.ScheduleReplayAsync(request, now, CancellationToken.None).ConfigureAwait(false);
        var conflict = await scheduler.ScheduleReplayAsync(
            request with { ReasonCode = "different-replay" }, now, CancellationToken.None).ConfigureAwait(false);
        var live = await scheduler.ScheduleLiveAsync(source.Id, now, CancellationToken.None).ConfigureAwait(false);
        var liveWithoutIncomingSource = await scheduler.ScheduleLiveAsync(
            calibrated.Id, now, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Created, created.Outcome);
        Assert.IsNotNull(created.Execution?.ExpandedAtUtc);
        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Running, created.Execution.Status);
        Assert.AreEqual(1, created.Execution.Jobs.Count);
        Assert.AreEqual(now, created.Execution.Jobs.Single().ResolutionDeadlineUtc);
        Assert.AreEqual(
            definition.Nodes.Single().Window!.MinimumInputCount,
            created.Execution.Jobs.Single().MinimumInputCount,
            "the window's minimum cardinality is frozen on the node so resolution can enforce it");
        CollectionAssert.AreEqual(
            new[] { created.Execution.Jobs.Single().Id }, windowResolver.ResolvedJobIds.ToArray());
        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Existing, existing.Outcome);
        Assert.AreEqual(created.Execution.Id, existing.Execution!.Id);
        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Conflict, conflict.Outcome);
        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Created, live.Outcome);
        Assert.AreEqual(CentralProcessingGraphExecutionClass.Live, live.Execution!.ExecutionClass);
        Assert.AreEqual(now.AddTicks(definition.Nodes.Single().Window!.TimeoutTicks),
            live.Execution.Jobs.Single().ResolutionDeadlineUtc);
        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.NotApplicable, liveWithoutIncomingSource.Outcome);
    }

    [TestMethod]
    public async Task ScheduleLiveReportsWhetherTheEffectiveGraphCoversTransientValidation()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        var recipeCatalog = new CentralDerivativeRecipeCatalog(new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            SourceRole = FrameArtifactRole.Raw
        });
        var registry = new CentralProcessingGraphNodeRegistry(recipeCatalog);
        var seededBasic = DatabaseSeeder.CreateBasicCentralProcessingGraph();
        Assert.IsFalse(seededBasic.Nodes.Any(node => node.Id == "TransientDetection"),
            "the seeded graph identity must stay config-independent");
        var withTransient = DatabaseSeeder.CreateBasicCentralProcessingGraph(recipeCatalog);
        var previewOnly = withTransient with
        {
            Name = "preview-only",
            Nodes = [.. withTransient.Nodes.Where(node => node.Id == "Preview")]
        };
        var previewAndTransient = withTransient with
        {
            Name = "preview-and-transient",
            Nodes = [.. withTransient.Nodes.Where(node => node.Id is "Preview" or "TransientDetection")]
        };
        var previewAndTransientAwaitingCalibrated = previewAndTransient with
        {
            Name = "preview-and-transient-awaiting",
            Sources =
            [
                .. previewAndTransient.Sources,
                new ProcessingGraphSourceDefinition("$calibrated",
                    [new ProcessingGraphProductContract(
                        FrameArtifactRole.Calibrated, "source", ProcessingProductKind.PixelData)])
            ]
        };
        var uncovered = AddAssignedArtifact(context, "uncovered", previewOnly, registry, now);
        var covered = AddAssignedArtifact(context, "covered", previewAndTransient, registry, now);
        var awaiting = AddAssignedArtifact(context, "awaiting", previewAndTransientAwaitingCalibrated, registry, now);
        await context.SaveChangesAsync().ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var scheduler = CreateScheduler(context, telemetry, catalogTelemetry, nodeRegistry: registry);

        var uncoveredResult = await scheduler.ScheduleLiveAsync(uncovered.Id, now, CancellationToken.None)
            .ConfigureAwait(false);
        var uncoveredExisting = await scheduler.ScheduleLiveAsync(uncovered.Id, now, CancellationToken.None)
            .ConfigureAwait(false);
        var coveredResult = await scheduler.ScheduleLiveAsync(covered.Id, now, CancellationToken.None)
            .ConfigureAwait(false);
        var awaitingResult = await scheduler.ScheduleLiveAsync(awaiting.Id, now, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Created, uncoveredResult.Outcome);
        Assert.IsFalse(uncoveredResult.CoversTransientValidation);
        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Existing, uncoveredExisting.Outcome);
        Assert.IsFalse(uncoveredExisting.CoversTransientValidation);
        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Created, coveredResult.Outcome);
        Assert.IsTrue(coveredResult.CoversTransientValidation);
        Assert.IsTrue(coveredResult.Execution!.Jobs.Any(job => job.RecipeName == CentralTransientRuntime.RecipeName));
        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.AwaitingSources, awaitingResult.Outcome);
        Assert.IsTrue(awaitingResult.CoversTransientValidation);
        var transientRecipe = recipeCatalog.GetTransientRecipe(FrameArtifactRole.Raw);
        Assert.IsTrue(CentralDerivativeJobScheduler.SchedulesLegacyTransientRecipe(uncoveredResult, transientRecipe));
        Assert.IsTrue(CentralDerivativeJobScheduler.SchedulesLegacyTransientRecipe(uncoveredExisting, transientRecipe));
        Assert.IsFalse(CentralDerivativeJobScheduler.SchedulesLegacyTransientRecipe(coveredResult, transientRecipe));
        Assert.IsFalse(CentralDerivativeJobScheduler.SchedulesLegacyTransientRecipe(awaitingResult, transientRecipe));
        Assert.IsFalse(CentralDerivativeJobScheduler.SchedulesLegacyTransientRecipe(uncoveredResult, transientRecipe: null));
        Assert.IsFalse(CentralDerivativeJobScheduler.SchedulesLegacyTransientRecipe(
            new(CentralProcessingGraphScheduleOutcome.NotApplicable), transientRecipe));
        Assert.IsTrue(CentralDerivativeJobScheduler.SchedulesLegacyTransientRecipe(
            new(CentralProcessingGraphScheduleOutcome.AwaitingSources), transientRecipe));
        Assert.IsTrue(CentralDerivativeJobScheduler.SchedulesLegacyTransientRecipe(
            new(CentralProcessingGraphScheduleOutcome.Invalid, ReasonCode: "revision-invalid"), transientRecipe));
    }

    [TestMethod]
    public async Task ExpansionFreezesAnnotationIdentityFromAnchorSceneProvenanceAndProjectsItToConsumers()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        var registry = new CentralProcessingGraphNodeRegistry(new CentralDerivativeRecipeCatalog());
        var definition = CreateAnnotationConsumerGraph();
        var provenance = new SceneProvenance(
            "scene-1", "rig-v1", "catalog", "1", new string('A', 64), "model", "1", "1", "1",
            Objects: [new ProjectedObjectProvenance("star:1", "Vega", 10, 12, 0.03)]);
        var annotated = AddAssignedArtifact(context, "annotated", definition, registry, now);
        annotated.Frame!.SceneProvenanceJson = JsonSerializer.Serialize(provenance, JsonSerializerOptions.Web);
        var unannotated = AddAssignedArtifact(context, "unannotated", definition, registry, now);
        Assert.IsNull(unannotated.Frame!.SceneProvenanceJson);
        await context.SaveChangesAsync().ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var scheduler = CreateScheduler(context, telemetry, catalogTelemetry, nodeRegistry: registry);
        var annotationOptions = definition.Nodes.Single(node => node.Id == "Annotation").EffectiveOptions;
        var requested = BuiltInProcessingRecipes.CreateRequestedIdentity(
            BuiltInProcessingRecipes.Annotation, annotationOptions, ProcessingInputSelector.Raw()).IdentitySha256;
        var runtimeAnnotation = CentralDerivativeJobExecutor.CreateAnnotation(annotated.Frame.SceneProvenanceJson);
        Assert.IsNotNull(runtimeAnnotation);
        var actual = BuiltInProcessingRecipes.CreateExecutionIdentity(
            BuiltInProcessingRecipes.Annotation, annotationOptions, ProcessingInputSelector.Raw(), runtimeAnnotation)
            .IdentitySha256;
        Assert.AreNotEqual(requested, actual, "the runtime scene annotation is part of the actual recipe identity");

        var withProvenance = await scheduler.ScheduleLiveAsync(annotated.Id, now, CancellationToken.None)
            .ConfigureAwait(false);
        var withoutProvenance = await scheduler.ScheduleLiveAsync(unannotated.Id, now, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Created, withProvenance.Outcome);
        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Created, withoutProvenance.Outcome);
        AssertProjection(withProvenance.Execution!, annotated.Frame.SceneProvenanceJson, expectedAnnotationIdentity: actual);
        AssertProjection(withoutProvenance.Execution!, null, expectedAnnotationIdentity: requested);

        void AssertProjection(
            CentralProcessingGraphExecution execution,
            string? sceneProvenanceJson,
            string expectedAnnotationIdentity)
        {
            var annotation = execution.Jobs.Single(job => job.GraphNodeId == "Annotation");
            var consumer = execution.Jobs.Single(job => job.GraphNodeId == "Preview");
            Assert.AreEqual(requested, annotation.RequestedRecipeIdentitySha256);
            Assert.AreEqual(expectedAnnotationIdentity, annotation.ExpectedRecipeIdentitySha256);
            // The frozen decision is authoritative for the lease: provenance frozen at expansion reaches the executor,
            // provenance the frame acquires afterwards does not (the node then skips exactly as frozen).
            Assert.AreEqual(sceneProvenanceJson, CentralDerivativeJobExecutor.ResolveLeaseSceneProvenance(
                execution.Id, annotation.RecipeName, annotation.RequestedRecipeIdentitySha256,
                annotation.ExpectedRecipeIdentitySha256, annotated.Frame!.SceneProvenanceJson));
            var expectedSelector = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(
                ProcessingInputSelector.RecipeResult(
                    FrameArtifactRole.AnnotatedPreview,
                    CentralDerivativeRecipeCatalog.AnnotatedPreviewVariant,
                    expectedAnnotationIdentity))).GetRawText();
            Assert.AreEqual(expectedSelector, consumer.InputSelectorJson,
                "the dependent primary selector pins the producer's frozen (annotation-adjusted) identity");
            Assert.AreEqual(expectedSelector, consumer.InputRequirements.Single().SelectorJson);
            var consumerOptions = definition.Nodes.Single(node => node.Id == "Preview").EffectiveOptions;
            var consumerRequested = BuiltInProcessingRecipes.CreateRequestedIdentity(
                BuiltInProcessingRecipes.EncodedPreview,
                consumerOptions,
                ProcessingInputSelector.RecipeResult(
                    FrameArtifactRole.AnnotatedPreview,
                    CentralDerivativeRecipeCatalog.AnnotatedPreviewVariant,
                    expectedAnnotationIdentity)).IdentitySha256;
            Assert.AreEqual(consumerRequested, consumer.RequestedRecipeIdentitySha256);
            Assert.IsTrue(string.Equals(consumerRequested, consumer.ExpectedRecipeIdentitySha256, StringComparison.Ordinal),
                "a consumer without auxiliaries or annotation keeps its requested identity as the expectation");
        }
    }

    [TestMethod]
    public void ExpectedRecipeIdentityFreezesAnnotationOnlyForAnnotationBearingBuiltInNodes()
    {
        var provenance = JsonSerializer.Serialize(new SceneProvenance(
            "scene-1", "rig-v1", "catalog", "1", new string('A', 64), "model", "1", "1", "1",
            Objects: [new ProjectedObjectProvenance("star:1", "Vega", 10, 12, 0.03)]), JsonSerializerOptions.Web);
        var annotationOptions = BuiltInProcessingRecipes.NormalizeOptions(
            BuiltInProcessingRecipes.Annotation, CaptureContractJson.SerializeToElement(new AnnotationRecipeOptions()));
        var previewOptions = BuiltInProcessingRecipes.NormalizeOptions(
            BuiltInProcessingRecipes.EncodedPreview, CaptureContractJson.SerializeToElement(new EncodedPreviewOptions()));
        var selector = ProcessingInputSelector.Raw();
        var annotationRequested = BuiltInProcessingRecipes.CreateRequestedIdentity(
            BuiltInProcessingRecipes.Annotation, annotationOptions, selector).IdentitySha256;
        var previewRequested = BuiltInProcessingRecipes.CreateRequestedIdentity(
            BuiltInProcessingRecipes.EncodedPreview, previewOptions, selector).IdentitySha256;
        var annotation = CentralDerivativeJobExecutor.CreateAnnotation(provenance);
        var auxiliary = new ProcessingAuxiliaryInput(
            "environment", ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: "schema-v1", IdentitySha256: new string('B', 64));

        Assert.AreEqual(
            BuiltInProcessingRecipes.CreateExecutionIdentity(
                BuiltInProcessingRecipes.Annotation, annotationOptions, selector, annotation).IdentitySha256,
            CentralProcessingGraphScheduler.CreateExpectedRecipeIdentity(
                CentralProcessingGraphNodeHandlerKind.BuiltInRecipe, BuiltInProcessingRecipes.Annotation,
                annotationOptions, selector, annotationRequested, [], provenance));
        Assert.AreEqual(annotationRequested, CentralProcessingGraphScheduler.CreateExpectedRecipeIdentity(
            CentralProcessingGraphNodeHandlerKind.BuiltInRecipe, BuiltInProcessingRecipes.Annotation,
            annotationOptions, selector, annotationRequested, [], null),
            "no scene provenance at expansion leaves the requested identity as the frozen expectation");
        Assert.AreEqual(annotationRequested, CentralProcessingGraphScheduler.CreateExpectedRecipeIdentity(
            CentralProcessingGraphNodeHandlerKind.BuiltInRecipe, BuiltInProcessingRecipes.Annotation,
            annotationOptions, selector, annotationRequested, [], "{\"sceneId\":\"scene-1\"}"),
            "provenance without objects or segments yields no runtime annotation");
        Assert.AreEqual(previewRequested, CentralProcessingGraphScheduler.CreateExpectedRecipeIdentity(
            CentralProcessingGraphNodeHandlerKind.BuiltInRecipe, BuiltInProcessingRecipes.EncodedPreview,
            previewOptions, selector, previewRequested, [], provenance),
            "only annotation-bearing recipes take the runtime annotation input");
        Assert.AreEqual(
            BuiltInProcessingRecipes.CreateExecutionIdentity(
                BuiltInProcessingRecipes.EncodedPreview, previewOptions, selector, auxiliaryInputs: [auxiliary])
                .IdentitySha256,
            CentralProcessingGraphScheduler.CreateExpectedRecipeIdentity(
                CentralProcessingGraphNodeHandlerKind.BuiltInRecipe, BuiltInProcessingRecipes.EncodedPreview,
                previewOptions, selector, previewRequested, [auxiliary], provenance),
            "auxiliary-bound nodes keep binding their auxiliaries exactly as before");
        Assert.AreEqual(new string('C', 64), CentralProcessingGraphScheduler.CreateExpectedRecipeIdentity(
            CentralProcessingGraphNodeHandlerKind.TransientValidation, CentralTransientRuntime.RecipeName,
            annotationOptions, selector, new string('C', 64), [auxiliary], provenance),
            "transient validation nodes always keep the catalog's requested identity");
    }

    /// <summary>A graph in which a node consumes the output of the annotation-bearing node.</summary>
    private static ProcessingGraphDefinition CreateAnnotationConsumerGraph()
    {
        _ = BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.Annotation, out var annotationDefinition);
        _ = BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.EncodedPreview, out var previewDefinition);
        var annotationOptions = BuiltInProcessingRecipes.NormalizeOptions(
            BuiltInProcessingRecipes.Annotation, CaptureContractJson.SerializeToElement(new AnnotationRecipeOptions()));
        var previewOptions = BuiltInProcessingRecipes.NormalizeOptions(
            BuiltInProcessingRecipes.EncodedPreview, CaptureContractJson.SerializeToElement(new EncodedPreviewOptions()));
        return new(
            ProcessingGraphSchemaVersions.Current,
            "annotation-consumer",
            "1",
            [new ProcessingGraphSourceDefinition(
                "$raw",
                [new ProcessingGraphProductContract(FrameArtifactRole.Raw, "source", ProcessingProductKind.PixelData)])],
            [
                new ProcessingGraphNodeDefinition(
                    "Annotation",
                    BuiltInProcessingRecipes.Annotation,
                    CentralDerivativeRecipeCatalog.AnnotatedPreviewRecipeVersion,
                    annotationDefinition!.OperationKind,
                    true,
                    ProcessingGraphNodeFailurePolicy.Required,
                    0,
                    annotationOptions,
                    [new ProcessingGraphDependencyDefinition("$raw")],
                    [new ProcessingGraphInputContract([FrameArtifactRole.Raw], [ProcessingProductKind.PixelData], [], [], [])],
                    [new ProcessingGraphProductContract(
                        FrameArtifactRole.AnnotatedPreview,
                        CentralDerivativeRecipeCatalog.AnnotatedPreviewVariant,
                        ProcessingProductKind.PixelData,
                        annotationDefinition,
                        MediaType: "image/jpeg")],
                    null,
                    ImmutableArray<string>.Empty,
                    [ProcessingGraphHosts.LogicHost]),
                new ProcessingGraphNodeDefinition(
                    "Preview",
                    BuiltInProcessingRecipes.EncodedPreview,
                    CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
                    previewDefinition!.OperationKind,
                    true,
                    ProcessingGraphNodeFailurePolicy.Required,
                    10,
                    previewOptions,
                    [new ProcessingGraphDependencyDefinition("Annotation")],
                    [new ProcessingGraphInputContract(
                        [FrameArtifactRole.AnnotatedPreview],
                        [ProcessingProductKind.PixelData],
                        [CentralDerivativeRecipeCatalog.AnnotatedPreviewVariant],
                        [BuiltInProcessingRecipes.Annotation],
                        [])],
                    [new ProcessingGraphProductContract(
                        FrameArtifactRole.Preview,
                        CentralDerivativeRecipeCatalog.PreviewVariant,
                        ProcessingProductKind.PixelData,
                        previewDefinition,
                        MediaType: "image/jpeg")],
                    null,
                    ImmutableArray<string>.Empty,
                    [ProcessingGraphHosts.LogicHost])
            ]);
    }

    private static CentralArtifact AddAssignedArtifact(
        ApplicationDbContext context,
        string slug,
        ProcessingGraphDefinition definition,
        ICentralProcessingGraphNodeRegistry registry,
        DateTimeOffset now)
    {
        var artifact = CreateArtifact();
        var observatoryId = Guid.NewGuid();
        var camera = new LogicalCamera
        {
            ObservatoryId = observatoryId,
            Slug = slug,
            Name = slug,
            Description = "Test",
            CreatedAtUtc = now.AddDays(-1),
            CreatedByUserId = "operator"
        };
        var installation = new LogicalCameraInstallation
        {
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            RegistrationId = Guid.NewGuid(),
            InstallationPublicId = Guid.NewGuid(),
            AssignedAtUtc = now.AddDays(-1),
            AssignedByUserId = "operator",
            AssignmentReasonCode = "test"
        };
        camera.Installations.Add(installation);
        artifact.Frame!.ObservatoryId = observatoryId;
        artifact.Frame.RegistrationId = installation.RegistrationId;
        artifact.Frame.LogicalCameraInstallation = installation;
        artifact.Frame.LogicalCameraInstallationId = installation.Id;
        artifact.Frame.CaptureSequence = 100;
        artifact.Frame.Artifacts.Add(artifact);
        var portable = ProcessingGraphCompiler.Compile(definition);
        Assert.IsTrue(portable.IsValid, string.Join(Environment.NewLine, portable.Diagnostics));
        var central = ProcessingGraphCompiler.Compile(
            definition, new(ProcessingGraphHosts.LogicHost, registry.Capabilities));
        Assert.IsTrue(central.IsValid, string.Join(Environment.NewLine, central.Diagnostics));
        Assert.IsTrue(registry.Validate(central.Plan!));
        var revision = new CentralProcessingGraphRevision
        {
            Name = definition.Name,
            Revision = definition.Revision,
            DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)),
            DefinitionIdentitySha256 = portable.Plan!.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = portable.Plan.PlanIdentitySha256,
            CentralPlanIdentitySha256 = central.Plan!.PlanIdentitySha256,
            CreatedAtUtc = now.AddMinutes(-2),
            CreatedByUserId = "operator",
            PublishedAtUtc = now.AddMinutes(-1),
            PublishedByUserId = "operator"
        };
        var assignment = new CentralProcessingGraphAssignment
        {
            Revision = revision,
            RevisionId = revision.Id,
            TargetHost = CentralProcessingGraphTargetHost.Central,
            Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
            ObservatoryId = observatoryId,
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now.AddMinutes(-1),
            ActorUserId = "operator",
            ReasonCode = "test"
        };
        revision.Assignments.Add(assignment);
        context.AddRange(camera, installation, artifact.Frame, artifact, revision, assignment);
        return artifact;
    }

    [TestMethod]
    public void ConvergenceFailureClassificationSeparatesDatabaseFaultsFromGraphState()
    {
        Assert.IsTrue(CentralProcessingGraphScheduler.IsDatabaseFailure(new DbUpdateException("update")));
        Assert.IsTrue(CentralProcessingGraphScheduler.IsDatabaseFailure(new DbUpdateConcurrencyException("race")));
        Assert.IsTrue(CentralProcessingGraphScheduler.IsDatabaseFailure(new TimeoutException("command timeout")));
        Assert.IsTrue(CentralProcessingGraphScheduler.IsDatabaseFailure(
            new InvalidOperationException("wrapped", new TimeoutException("inner"))));
        Assert.IsTrue(CentralProcessingGraphScheduler.IsDatabaseFailure(
            new AggregateException(new InvalidOperationException("state"), new DbUpdateException("update"))));
        // SqlClient surfaces an aborted/timed-out command as (Task)OperationCanceledException when the caller's token
        // is still live; ConvergeBatchAsync rethrows only when its own token is canceled.
        Assert.IsTrue(CentralProcessingGraphScheduler.IsDatabaseFailure(new OperationCanceledException("command aborted")));
        Assert.IsTrue(CentralProcessingGraphScheduler.IsDatabaseFailure(new TaskCanceledException("command timeout")));
        Assert.IsTrue(CentralProcessingGraphScheduler.IsDatabaseFailure(
            new InvalidOperationException("wrapped", new TaskCanceledException("inner"))));
        Assert.IsFalse(CentralProcessingGraphScheduler.IsDatabaseFailure(
            new CentralDerivativeJobStateException("state", new OperationCanceledException("shadowed"))));
        Assert.IsFalse(CentralProcessingGraphScheduler.IsDatabaseFailure(
            new CentralDerivativeJobStateException("The published processing graph plan identity is invalid.")));
        Assert.IsFalse(CentralProcessingGraphScheduler.IsDatabaseFailure(
            new CentralDerivativeJobStateException("state", new TimeoutException("shadowed"))));
        Assert.IsFalse(CentralProcessingGraphScheduler.IsDatabaseFailure(new InvalidOperationException("corrupt")));
        Assert.IsFalse(CentralProcessingGraphScheduler.IsDatabaseFailure(new FormatException("frozen plan json")));
    }

    [TestMethod]
    public async Task ConvergeBatchRecordsDependencyFailureOnlyForDatabaseFaults()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var source = CreateArtifact();
        var execution = CreateExecution(source, now, expectedNodeCount: 1, expectedDependencyCount: 1);
        var sourceRow = AddSource(execution, source, now);
        var job = AddJob(execution, source, 0, ProcessingGraphNodeFailurePolicy.Required, now);
        AddDependency(execution, sourceRow, job, now);
        // A transient node whose frozen options requirement has no backing validation row is corrupt graph state:
        // convergence fails with a non-database exception.
        job.RecipeName = CentralTransientRuntime.RecipeName;
        job.InputRequirements.Add(new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Ordinal = 1,
            BindingName = "transient-extraction-options",
            SourceKind = CentralDerivativeInputSourceKind.Canonical,
            IsRequired = true,
            SelectorJson = "{}",
            CompatibilityMode = CentralDerivativeCompatibilityMode.None,
            ExpectedAgentId = "agent",
            ExpectedRigId = "rig",
            ResolutionState = CentralDerivativeInputResolutionState.Waiting
        });
        context.AddRange(source.Frame!, source, execution);
        await context.SaveChangesAsync().ConfigureAwait(false);
        await SealAsync(context, execution, now).ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var scheduler = CreateScheduler(context, telemetry, catalogTelemetry);

        await scheduler.ConvergeBatchAsync(now.AddSeconds(1), CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(telemetry.HasRecentDependencyFailure(now.AddSeconds(1), TimeSpan.FromMinutes(1)),
            "a graph-state convergence failure is not a database dependency failure");
        // The failed execution is still rotated behind newer recovery work (the in-memory provider has no
        // transactions, so the durable Running step is not rolled back here; SQL Server coverage is in the
        // integration suite).
        var rotated = await context.CentralProcessingGraphExecutions.AsNoTracking().SingleAsync().ConfigureAwait(false);
        Assert.AreEqual(now.AddSeconds(1), rotated.UpdatedAtUtc);
        Assert.IsNull(rotated.CompletedAtUtc);
    }

    [TestMethod]
    public async Task ScheduleReplayRejectsEveryInvalidRequestShapeAndRevision()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var scheduler = CreateScheduler(context, telemetry, catalogTelemetry);
        var artifactId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var valid = new CentralProcessingGraphReplayRequest(
            revisionId, [artifactId], "operator", "replay-key", "test-replay");
        var invalidRequests = new[]
        {
            valid with { SourceCentralArtifactIds = [] },
            valid with
            {
                SourceCentralArtifactIds = Enumerable.Range(0, ProcessingGraphCompiler.MaximumSources + 1)
                    .Select(static _ => Guid.NewGuid()).ToArray()
            },
            valid with { SourceCentralArtifactIds = [artifactId, artifactId] },
            valid with { ActorId = " " },
            valid with { ActorId = new string('a', 451) },
            valid with { IdempotencyKey = " " },
            valid with { IdempotencyKey = new string('k', 257) },
            valid with { ReasonCode = " " },
            valid with { ReasonCode = new string('r', 129) }
        };
        foreach (var request in invalidRequests)
        {
            var result = await scheduler.ScheduleReplayAsync(request, now, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Invalid, result.Outcome);
            Assert.AreEqual("invalid-replay", result.ReasonCode);
        }
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => scheduler.ScheduleReplayAsync(
            null!, now, CancellationToken.None)).ConfigureAwait(false);

        var unpublished = await scheduler.ScheduleReplayAsync(valid, now, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Invalid, unpublished.Outcome);
        Assert.AreEqual("revision-not-published", unpublished.ReasonCode);

        var malformedRevision = new CentralProcessingGraphRevision
        {
            Id = revisionId,
            Name = "malformed",
            Revision = "1",
            DefinitionJson = "{",
            DefinitionIdentitySha256 = new string('A', 64),
            PortablePlanIdentitySha256 = new string('B', 64),
            CentralPlanIdentitySha256 = new string('C', 64),
            CreatedAtUtc = now.AddMinutes(-1),
            CreatedByUserId = "operator",
            PublishedAtUtc = now,
            PublishedByUserId = "operator"
        };
        context.Add(malformedRevision);
        await context.SaveChangesAsync().ConfigureAwait(false);

        var malformed = await scheduler.ScheduleReplayAsync(valid, now, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Invalid, malformed.Outcome);
        Assert.AreEqual("revision-invalid", malformed.ReasonCode);
    }

    [TestMethod]
    public async Task ScheduleLiveRejectsEveryUnusableCameraAndArtifactShape()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var artifacts = new List<CentralArtifact>();

        CentralArtifact AddConfiguredArtifact(string name)
        {
            var artifact = CreateArtifact();
            var observatoryId = Guid.NewGuid();
            var camera = new LogicalCamera
            {
                ObservatoryId = observatoryId,
                Slug = name,
                Name = name,
                Description = "Test",
                CreatedAtUtc = now.AddDays(-1),
                CreatedByUserId = "operator"
            };
            var installation = new LogicalCameraInstallation
            {
                LogicalCamera = camera,
                LogicalCameraId = camera.Id,
                RegistrationId = Guid.NewGuid(),
                InstallationPublicId = Guid.NewGuid(),
                AssignedAtUtc = now.AddDays(-1),
                AssignedByUserId = "operator",
                AssignmentReasonCode = "test"
            };
            camera.Installations.Add(installation);
            artifact.Frame!.ObservatoryId = observatoryId;
            artifact.Frame.RegistrationId = installation.RegistrationId;
            artifact.Frame.LogicalCameraInstallation = installation;
            artifact.Frame.LogicalCameraInstallationId = installation.Id;
            artifact.Frame.Artifacts.Add(artifact);
            context.Add(camera);
            context.Add(artifact.Frame);
            context.Add(artifact);
            artifacts.Add(artifact);
            return artifact;
        }

        AddConfiguredArtifact("object-pending").ObjectState = CentralArtifactObjectState.Pending;
        AddConfiguredArtifact("reconstruction-pending").ReconstructionState = CentralReconstructionState.PendingReference;
        AddConfiguredArtifact("wrong-role").Role = FrameArtifactRole.Preview;
        var retired = AddConfiguredArtifact("retired");
        retired.Frame!.LogicalCameraInstallation!.RetiredAtUtc = now;
        retired.Frame.LogicalCameraInstallation.RetiredByUserId = "operator";
        retired.Frame.LogicalCameraInstallation.RetirementReasonCode = "test";
        var deactivated = AddConfiguredArtifact("deactivated");
        deactivated.Frame!.LogicalCameraInstallation!.LogicalCamera!.DeactivatedAtUtc = now;
        var wrongObservatory = AddConfiguredArtifact("wrong-observatory");
        wrongObservatory.Frame!.ObservatoryId = Guid.NewGuid();
        _ = AddConfiguredArtifact("valid-unassigned");
        var awaitingSources = AddConfiguredArtifact("awaiting-sources");
        // Same Raw role as the published source contract, but the contract pins a specific variant the artifact does
        // not carry: the full contract, not the role alone, decides whether an artifact may be frozen as a source.
        var variantMismatch = AddConfiguredArtifact("variant-mismatch");
        var pinnedSource = CreatePreviewGraph().Sources[0];
        var variantDefinition = CreatePreviewGraph() with
        {
            Name = "variant-mismatch",
            Sources =
            [
                pinnedSource with
                {
                    Outputs = [pinnedSource.Outputs[0] with { Variant = "pinned-variant" }]
                }
            ]
        };
        var variantPortable = ProcessingGraphCompiler.Compile(variantDefinition).Plan!;
        var variantCentral = ProcessingGraphCompiler.Compile(
            variantDefinition, new(ProcessingGraphHosts.LogicHost, [])).Plan!;
        var variantRevision = new CentralProcessingGraphRevision
        {
            Name = variantDefinition.Name,
            Revision = variantDefinition.Revision,
            DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(variantDefinition)),
            DefinitionIdentitySha256 = variantPortable.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = variantPortable.PlanIdentitySha256,
            CentralPlanIdentitySha256 = variantCentral.PlanIdentitySha256,
            CreatedAtUtc = now.AddMinutes(-2),
            CreatedByUserId = "operator",
            PublishedAtUtc = now.AddMinutes(-1),
            PublishedByUserId = "operator"
        };
        var variantAssignment = new CentralProcessingGraphAssignment
        {
            Revision = variantRevision,
            RevisionId = variantRevision.Id,
            TargetHost = CentralProcessingGraphTargetHost.Central,
            Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
            ObservatoryId = variantMismatch.Frame!.ObservatoryId,
            LogicalCameraId = variantMismatch.Frame.LogicalCameraInstallation!.LogicalCameraId,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now.AddMinutes(-1),
            ActorUserId = "operator",
            ReasonCode = "test"
        };
        variantRevision.Assignments.Add(variantAssignment);
        context.AddRange(variantRevision, variantAssignment);
        var awaitingDefinition = CreatePreviewGraph() with
        {
            Sources =
            [
                CreatePreviewGraph().Sources[0],
                new ProcessingGraphSourceDefinition("$calibrated",
                    [new ProcessingGraphProductContract(
                        FrameArtifactRole.Calibrated, "source", ProcessingProductKind.PixelData)])
            ]
        };
        var awaitingPortable = ProcessingGraphCompiler.Compile(awaitingDefinition).Plan!;
        var awaitingCentral = ProcessingGraphCompiler.Compile(
            awaitingDefinition, new(ProcessingGraphHosts.LogicHost, [])).Plan!;
        var awaitingRevision = new CentralProcessingGraphRevision
        {
            Name = awaitingDefinition.Name,
            Revision = awaitingDefinition.Revision,
            DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(awaitingDefinition)),
            DefinitionIdentitySha256 = awaitingPortable.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = awaitingPortable.PlanIdentitySha256,
            CentralPlanIdentitySha256 = awaitingCentral.PlanIdentitySha256,
            CreatedAtUtc = now.AddMinutes(-2),
            CreatedByUserId = "operator",
            PublishedAtUtc = now.AddMinutes(-1),
            PublishedByUserId = "operator"
        };
        var awaitingAssignment = new CentralProcessingGraphAssignment
        {
            Revision = awaitingRevision,
            RevisionId = awaitingRevision.Id,
            TargetHost = CentralProcessingGraphTargetHost.Central,
            Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
            ObservatoryId = awaitingSources.Frame!.ObservatoryId,
            LogicalCameraId = awaitingSources.Frame.LogicalCameraInstallation!.LogicalCameraId,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now.AddMinutes(-1),
            ActorUserId = "operator",
            ReasonCode = "test"
        };
        awaitingRevision.Assignments.Add(awaitingAssignment);
        context.AddRange(awaitingRevision, awaitingAssignment);
        var noInstallation = CreateArtifact();
        context.AddRange(noInstallation.Frame!, noInstallation);
        artifacts.Add(noInstallation);
        await context.SaveChangesAsync().ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var scheduler = CreateScheduler(context, telemetry, catalogTelemetry);

        foreach (var artifact in artifacts.Where(item => item.Id != awaitingSources.Id))
        {
            Assert.AreEqual(CentralProcessingGraphScheduleOutcome.NotApplicable,
                (await scheduler.ScheduleLiveAsync(artifact.Id, now, CancellationToken.None)
                    .ConfigureAwait(false)).Outcome);
        }
        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.AwaitingSources,
            (await scheduler.ScheduleLiveAsync(awaitingSources.Id, now, CancellationToken.None)
                .ConfigureAwait(false)).Outcome);
    }

    [TestMethod]
    public void MinimumInputCountCountsDistinctResolvedPositionsAcrossBindings()
    {
        // A window node with a primary and an auxiliary binding expands two requirements per sequence offset.
        var job = new CentralDerivativeJob { MinimumInputCount = 3 };
        foreach (var offset in new[] { -2, -1, 0 })
        {
            foreach (var binding in new[] { "input", "auxiliary" })
            {
                job.InputRequirements.Add(new CentralDerivativeJobInputRequirement
                {
                    Job = job,
                    BindingName = binding,
                    SourceKind = CentralDerivativeInputSourceKind.Artifact,
                    SequenceOffset = offset,
                    ResolutionState = offset == 0
                        ? CentralDerivativeInputResolutionState.Missing
                        : CentralDerivativeInputResolutionState.Resolved
                });
            }
        }
        // Four resolved requirements, but only two resolved positions: the minimum of three is not met.
        Assert.AreEqual(4, job.InputRequirements.Count(item =>
            item.ResolutionState == CentralDerivativeInputResolutionState.Resolved));
        Assert.AreEqual(2, CentralDerivativeWindowResolver.CountResolvedPositions(job));
        Assert.IsTrue(CentralDerivativeWindowResolver.IsBelowMinimumInputCount(job));

        // A position counts only when every binding at that offset resolved.
        var partial = job.InputRequirements.Single(item => item.SequenceOffset == 0 && item.BindingName == "input");
        partial.ResolutionState = CentralDerivativeInputResolutionState.Resolved;
        Assert.AreEqual(2, CentralDerivativeWindowResolver.CountResolvedPositions(job));
        Assert.IsTrue(CentralDerivativeWindowResolver.IsBelowMinimumInputCount(job));
        job.InputRequirements.Single(item => item.SequenceOffset == 0 && item.BindingName == "auxiliary")
            .ResolutionState = CentralDerivativeInputResolutionState.Resolved;
        Assert.AreEqual(3, CentralDerivativeWindowResolver.CountResolvedPositions(job));
        Assert.IsFalse(CentralDerivativeWindowResolver.IsBelowMinimumInputCount(job));

        // Canonical (non-artifact) requirements never count toward the window cardinality.
        job.InputRequirements.Add(new CentralDerivativeJobInputRequirement
        {
            Job = job,
            BindingName = "environment",
            SourceKind = CentralDerivativeInputSourceKind.EnvironmentalObservation,
            SequenceOffset = 1,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved
        });
        Assert.AreEqual(3, CentralDerivativeWindowResolver.CountResolvedPositions(job));
        // Artifact requirements without a sequence offset are not temporal positions and never count.
        job.InputRequirements.Add(new CentralDerivativeJobInputRequirement
        {
            Job = job,
            BindingName = "reference",
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            SequenceOffset = null,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved
        });
        Assert.AreEqual(3, CentralDerivativeWindowResolver.CountResolvedPositions(job));
        job.MinimumInputCount = 4;
        Assert.IsTrue(CentralDerivativeWindowResolver.IsBelowMinimumInputCount(job));
        job.MinimumInputCount = null;
        Assert.IsFalse(CentralDerivativeWindowResolver.IsBelowMinimumInputCount(job), "no frozen minimum imposes none");
    }

    [TestMethod]
    public void SourceContractMatchesRequiresEveryPinnedContractField()
    {
        var artifact = CreateArtifact();
        var contract = new ProcessingGraphProductContract(FrameArtifactRole.Raw, "source", ProcessingProductKind.PixelData);
        Assert.IsTrue(CentralProcessingGraphScheduler.SourceContractMatches(contract, artifact));
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(
            contract with { Role = FrameArtifactRole.Calibrated }, artifact));
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(
            contract with { Variant = "different" }, artifact));
        artifact.Variant = "deployment-calibrated";
        Assert.IsTrue(CentralProcessingGraphScheduler.SourceContractMatches(contract, artifact),
            "the acquisition placeholder accepts the role's agent-configured acquisition variant");
        Assert.IsTrue(CentralProcessingGraphScheduler.SourceContractMatches(
            contract with { Variant = "deployment-calibrated" }, artifact));
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(
            contract with { Variant = "other-pinned" }, artifact));
        artifact.Variant = null;
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(contract, artifact),
            "an artifact without reconstruction variant provenance never satisfies a source contract");
        artifact.Variant = "source";
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(
            contract with { ProductKind = ProcessingProductKind.Metadata }, artifact));
        Assert.IsTrue(CentralProcessingGraphScheduler.SourceContractMatches(
            contract with { MediaType = "APPLICATION/OCTET-STREAM" }, artifact));
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(
            contract with { MediaType = "image/jpeg" }, artifact));
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(
            contract with { SchemaVersion = "schema-v1" }, artifact), "a pinned schema needs a structured product");
        var recipe = new ProcessingRecipeDefinition("raw", "1.0.0", "impl-v1", ProcessingOperationKind.Transform);
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(
            contract with { Recipe = recipe }, artifact), "a pinned recipe needs recorded recipe provenance");
        artifact.Recipe = new CentralArtifactRecipe
        {
            Name = recipe.Name,
            SemanticVersion = recipe.SemanticVersion,
            ImplementationVersion = recipe.ImplementationVersion
        };
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(contract with { Recipe = recipe }, artifact),
            "a pinned recipe whose operation kind has no durable provenance never matches");
        var transform = new CentralSourceProvenance(ProcessingOperationKind.Transform, "[]");
        Assert.IsTrue(CentralProcessingGraphScheduler.SourceContractMatches(
            contract with { Recipe = recipe }, artifact, transform));
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(
            contract with { Recipe = recipe }, artifact, transform with { OperationKind = ProcessingOperationKind.Analyzer }),
            "the pinned recipe's operation kind must match the durable operation kind");
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(
            contract with { Recipe = recipe with { ImplementationVersion = "impl-v2" } }, artifact, transform));
        artifact.Recipe.Name = BuiltInProcessingRecipes.EncodedPreview;
        _ = BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.EncodedPreview, out var builtIn);
        Assert.AreEqual(builtIn!.OperationKind, CentralProcessingGraphScheduler.ResolveSourceProvenance(artifact, null).OperationKind,
            "an agent-published artifact derives its operation kind from the built-in recipe it names");
        Assert.AreEqual("[]", CentralProcessingGraphScheduler.ResolveSourceProvenance(artifact, null).AlgorithmsJson);
        var recorded = new Dictionary<Guid, CentralSourceProvenance>
        {
            [artifact.Id] = new(ProcessingOperationKind.Window, "[{\"name\":\"stack\",\"version\":\"2\"}]")
        };
        Assert.AreEqual(recorded[artifact.Id], CentralProcessingGraphScheduler.ResolveSourceProvenance(artifact, recorded),
            "central processing evidence is authoritative when present");
        artifact.Recipe.Name = BuiltInProcessingRecipes.EncodedPreview;
        recorded[artifact.Id] = new(null, "[{\"name\":\"stack\",\"version\":\"2\"}]");
        var fallback = CentralProcessingGraphScheduler.ResolveSourceProvenance(artifact, recorded);
        Assert.AreEqual(builtIn.OperationKind, fallback.OperationKind,
            "evidence without an operation kind falls back to the built-in recipe's operation kind");
        Assert.AreEqual(recorded[artifact.Id].AlgorithmsJson, fallback.AlgorithmsJson,
            "the recorded algorithms remain authoritative on fallback");
        artifact.Recipe.Name = "unknown-recipe";
        Assert.IsNull(CentralProcessingGraphScheduler.ResolveSourceProvenance(artifact, recorded).OperationKind,
            "no built-in definition leaves the operation kind unestablished");
        artifact.Recipe.Name = recipe.Name;
        var algorithm = new ProcessingAlgorithmIdentity("stack", "2");
        var pinnedAlgorithms = contract with { Algorithms = [algorithm] };
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(pinnedAlgorithms, artifact, transform),
            "pinned algorithms need matching durable algorithms");
        Assert.IsTrue(CentralProcessingGraphScheduler.SourceContractMatches(
            pinnedAlgorithms, artifact, transform with { AlgorithmsJson = "[{\"name\":\"stack\",\"version\":\"2\"}]" }));
        Assert.IsTrue(CentralProcessingGraphScheduler.SourceContractMatches(
            pinnedAlgorithms, artifact, transform with { AlgorithmsJson = "[{\"Name\":\"stack\",\"Version\":\"2\"}]" }),
            "structured-product algorithm JSON casing is accepted");
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(
            pinnedAlgorithms, artifact, transform with { AlgorithmsJson = "[{\"name\":\"stack\",\"version\":\"3\"}]" }));
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(
            pinnedAlgorithms, artifact, transform with { AlgorithmsJson = "not-json" }));
        Assert.IsTrue(CentralProcessingGraphScheduler.SourceContractMatches(
            contract, artifact, transform with { AlgorithmsJson = "[{\"name\":\"stack\",\"version\":\"2\"}]" }),
            "a contract that pins no algorithms accepts any durable algorithm list");
        artifact.StructuredProduct = new CentralStructuredProcessingProduct
        {
            ProductKind = ProcessingProductKind.PixelData.ToString(),
            ProductSchemaVersion = "schema-v1"
        };
        Assert.IsTrue(CentralProcessingGraphScheduler.SourceContractMatches(
            contract with { SchemaVersion = "schema-v1" }, artifact));
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(
            contract with { SchemaVersion = "schema-v2" }, artifact));
        artifact.StructuredProduct.ProductKind = ProcessingProductKind.Metadata.ToString();
        Assert.IsFalse(CentralProcessingGraphScheduler.SourceContractMatches(contract, artifact));
    }

    [TestMethod]
    public async Task CanonicalReplayFreezesEnvironmentalAndTransientInputsAcrossAllNodes()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        var observatoryId = Guid.NewGuid();
        var registrationId = Guid.NewGuid();
        var camera = new LogicalCamera
        {
            ObservatoryId = observatoryId,
            Slug = "canonical-camera",
            Name = "Canonical Camera",
            Description = "Test",
            CreatedAtUtc = now.AddDays(-1),
            CreatedByUserId = "operator"
        };
        var installation = new LogicalCameraInstallation
        {
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            RegistrationId = registrationId,
            InstallationPublicId = Guid.NewGuid(),
            AssignedAtUtc = now.AddDays(-1),
            AssignedByUserId = "operator",
            AssignmentReasonCode = "test"
        };
        camera.Installations.Add(installation);
        var raw = CreateArtifact();
        raw.Frame!.ObservatoryId = observatoryId;
        raw.Frame.RegistrationId = registrationId;
        raw.Frame.LogicalCameraInstallation = installation;
        raw.Frame.LogicalCameraInstallationId = installation.Id;
        raw.Frame.RigId = "canonical-rig";
        raw.Frame.CaptureSequence = 100;
        var calibrated = new CentralArtifact
        {
            CentralFrameId = raw.Frame.Id,
            Frame = raw.Frame,
            DevicePublicId = raw.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Calibrated,
            Variant = "calibrated",
            RecipeVersion = "calibrated-v1",
            ManifestSchemaVersion = "manifest-v1",
            MediaType = "application/x-hvo-linear-frame",
            ByteLength = 2,
            ChecksumSha256 = new string('2', 64),
            StorageReference = $"s3://skymonitor-artifacts/{Guid.NewGuid():N}",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReceivedAtUtc = now,
            CreatedUtc = now,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        raw.Frame.Artifacts.Add(raw);
        raw.Frame.Artifacts.Add(calibrated);
        var clearReference = CreateArtifact();
        clearReference.Frame!.ObservatoryId = observatoryId;
        clearReference.Frame.RegistrationId = registrationId;
        clearReference.Frame.RigId = raw.Frame.RigId;
        clearReference.Variant = "clear-reference";
        clearReference.ReceivedAtUtc = now.AddDays(-1);
        clearReference.CreatedUtc = now.AddDays(-1);
        var designation = new CentralClearReferenceDesignation
        {
            RegistrationId = registrationId,
            RigId = raw.Frame.RigId,
            CentralArtifactId = clearReference.Id,
            Artifact = clearReference,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now.AddDays(-1),
            UpdatedBy = "operator"
        };
        var recipeCatalog = new CentralDerivativeRecipeCatalog(new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            SourceRole = FrameArtifactRole.Raw
        });
        var registry = new CentralProcessingGraphNodeRegistry(recipeCatalog);
        var definition = DatabaseSeeder.CreateBasicCentralProcessingGraph(recipeCatalog);
        var portable = ProcessingGraphCompiler.Compile(definition).Plan!;
        var central = ProcessingGraphCompiler.Compile(
            definition, new(ProcessingGraphHosts.LogicHost, registry.Capabilities)).Plan!;
        var revision = new CentralProcessingGraphRevision
        {
            Name = definition.Name,
            Revision = definition.Revision,
            DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)),
            DefinitionIdentitySha256 = portable.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = portable.PlanIdentitySha256,
            CentralPlanIdentitySha256 = central.PlanIdentitySha256,
            CreatedAtUtc = now.AddMinutes(-2),
            CreatedByUserId = "operator",
            PublishedAtUtc = now.AddMinutes(-1),
            PublishedByUserId = "operator"
        };
        context.AddRange(camera, installation, raw.Frame, raw, calibrated, clearReference.Frame, clearReference,
            designation, revision);
        await context.SaveChangesAsync().ConfigureAwait(false);
        using var workerTelemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var scheduler = CreateScheduler(
            context,
            workerTelemetry,
            catalogTelemetry,
            nodeRegistry: registry,
            environmentalQuery: new StubEnvironmentalQueryService());
        var orderedSources = central.Sources.Select(source => source.Id switch
        {
            "$raw" => raw.Id,
            "$calibrated" => calibrated.Id,
            _ => throw new InvalidOperationException("Unexpected canonical graph source.")
        }).ToArray();

        var result = await scheduler.ScheduleReplayAsync(
            new(revision.Id, orderedSources, "operator", "canonical-replay", "test"),
            now,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Created, result.Outcome);
        Assert.HasCount(7, result.Execution!.Jobs);
        Assert.HasCount(central.Sources.Length, result.Execution.Sources);
        Assert.IsTrue(result.Execution.Jobs.Any(job => job.Inputs.Any(input =>
            input.CentralArtifactId == clearReference.Id)));
        Assert.IsTrue(result.Execution.Jobs.Any(job => job.CanonicalInputs.Any(input =>
            input.SchemaVersion == CloudAssessmentEnvironmentV1.CurrentSchemaVersion)));
        Assert.IsTrue(result.Execution.Jobs.Where(job => job.WaitKind == CentralDerivativeWaitKind.Window)
            .All(job => job.ResolutionDeadlineUtc == now));
        Assert.AreEqual(1, await context.CentralTransientValidationJobs.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ConvergeMaterializesFrozenSourceAndMakesNodeRunnable()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var source = CreateArtifact();
        var execution = CreateExecution(source, now, expectedNodeCount: 1, expectedDependencyCount: 1);
        var sourceRow = AddSource(execution, source, now);
        var job = AddJob(execution, source, 0, ProcessingGraphNodeFailurePolicy.Required, now);
        AddDependency(execution, sourceRow, job, now);
        context.AddRange(source.Frame!, source, execution);
        await context.SaveChangesAsync().ConfigureAwait(false);
        await SealAsync(context, execution, now).ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);

        await CreateScheduler(context, telemetry, catalogTelemetry).ConvergeAsync(
            execution.Id, now.AddSeconds(1), CancellationToken.None).ConfigureAwait(false);

        var persisted = await context.CentralDerivativeJobs.AsNoTracking()
            .Include(item => item.InputRequirements)
            .Include(item => item.Inputs)
            .SingleAsync(item => item.Id == job.Id).ConfigureAwait(false);
        Assert.AreEqual(CentralDerivativeJobStatus.Pending, persisted.Status);
        Assert.AreEqual(1, persisted.Inputs.Count);
        Assert.AreEqual(CentralDerivativeInputResolutionState.Resolved,
            persisted.InputRequirements.Single().ResolutionState);
        Assert.AreEqual(64, persisted.InputSetIdentitySha256!.Length);
        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Running,
            await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Where(item => item.Id == execution.Id)
                .Select(item => item.Status)
                .SingleAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ConvergeMaterializesVerifiedCanonicalDependency()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var source = CreateArtifact();
        var canonicalPayload = Encoding.UTF8.GetBytes("{\"value\":1}");
        var canonical = new CentralArtifact
        {
            CentralFrameId = source.Frame!.Id,
            Frame = source.Frame,
            DevicePublicId = source.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Metadata,
            Variant = "canonical",
            RecipeVersion = "metadata-v1",
            ManifestSchemaVersion = "manifest-v1",
            MediaType = "application/json",
            ByteLength = canonicalPayload.Length,
            ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(canonicalPayload),
            StorageReference = $"s3://skymonitor-artifacts/{Guid.NewGuid():N}",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReceivedAtUtc = now,
            CreatedUtc = now,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete,
            StructuredProduct = new CentralStructuredProcessingProduct
            {
                OutputIdentitySha256 = new string('A', 64),
                ProductKind = ProcessingProductKind.Metadata.ToString(),
                ProductSchemaVersion = "canonical-v1",
                ContentIdentitySha256 = new string('B', 64),
                AlgorithmsJson = "[]",
                CompatibilityJson = "{}",
                DescriptorJson = "{}"
            }
        };
        source.Frame.Artifacts.Add(source);
        source.Frame.Artifacts.Add(canonical);
        var execution = CreateExecution(source, now, expectedNodeCount: 2, expectedDependencyCount: 1);
        execution.ExpectedOutputCount = 1;
        _ = AddSource(execution, source, now);
        var producer = AddJob(execution, source, 0, ProcessingGraphNodeFailurePolicy.Required, now);
        producer.Status = CentralDerivativeJobStatus.Completed;
        producer.CompletedAtUtc = now;
        var output = CentralDerivativeJobOutput.CreateFromFrozenPlan(
            producer,
            0,
            new ProcessingGraphProductContract(
                FrameArtifactRole.Metadata, "canonical", ProcessingProductKind.Metadata));
        producer.Outputs.Add(output);
        var consumer = AddJob(execution, source, 1, ProcessingGraphNodeFailurePolicy.Required, now);
        var dependency = new CentralDerivativeJobDependency
        {
            Execution = execution,
            ExecutionId = execution.Id,
            ConsumerJob = consumer,
            ConsumerJobId = consumer.Id,
            ProducerJob = producer,
            ProducerJobId = producer.Id,
            ProducerOutputOrdinal = 0,
            ConsumerInputOrdinal = 0,
            ConsumerBindingName = "metadata",
            ConsumerBindingKind = ProcessingGraphInputBindingKind.CanonicalJson,
            Kind = ProcessingGraphDependencyKind.CanonicalJson,
            Required = true
        };
        var requirement = new CentralDerivativeJobInputRequirement
        {
            Job = consumer,
            CentralDerivativeJobId = consumer.Id,
            BindingName = "metadata",
            GraphDependency = dependency,
            GraphDependencyId = dependency.Id,
            GraphInputOrdinal = 0,
            GraphInputBindingKind = ProcessingGraphInputBindingKind.CanonicalJson,
            SourceKind = CentralDerivativeInputSourceKind.Canonical,
            IsRequired = true,
            SelectorJson = "{}",
            ExpectedAgentId = source.Frame.AgentId,
            ExpectedRigId = source.Frame.RigId,
            ResolutionState = CentralDerivativeInputResolutionState.Waiting
        };
        dependency.InputRequirements.Add(requirement);
        consumer.Dependencies.Add(dependency);
        consumer.InputRequirements.Add(requirement);
        execution.Dependencies.Add(dependency);
        context.AddRange(source.Frame, source, canonical, execution);
        await context.SaveChangesAsync().ConfigureAwait(false);
        output.ResultCentralArtifactId = canonical.Id;
        output.ResultArtifact = canonical;
        output.ResultOutputIdentitySha256 = canonical.StructuredProduct.OutputIdentitySha256;
        output.BoundAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        await SealAsync(context, execution, now).ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);

        await CreateScheduler(
            context,
            telemetry,
            catalogTelemetry,
            objectReader: new CanonicalObjectReader(canonicalPayload)).ConvergeAsync(
                execution.Id, now.AddSeconds(1), CancellationToken.None).ConfigureAwait(false);

        var persisted = await context.CentralDerivativeJobs.AsNoTracking()
            .Include(item => item.InputRequirements)
            .Include(item => item.CanonicalInputs)
            .SingleAsync(item => item.Id == consumer.Id).ConfigureAwait(false);
        Assert.AreEqual(CentralDerivativeJobStatus.Pending, persisted.Status);
        Assert.AreEqual(CentralDerivativeInputResolutionState.Resolved,
            persisted.InputRequirements.Single().ResolutionState);
        Assert.AreEqual("canonical-v1", persisted.CanonicalInputs.Single().SchemaVersion);
        Assert.AreEqual("{\"value\":1}", persisted.CanonicalInputs.Single().CanonicalJson);
    }

    [TestMethod]
    public async Task ConvergePropagatesRequiredProducerFailureAndFailsExecution()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var source = CreateArtifact();
        var execution = CreateExecution(source, now, expectedNodeCount: 2, expectedDependencyCount: 1);
        _ = AddSource(execution, source, now);
        var producer = AddJob(execution, source, 0, ProcessingGraphNodeFailurePolicy.Required, now);
        producer.Status = CentralDerivativeJobStatus.TerminalFailure;
        producer.CompletedAtUtc = now;
        producer.LastFailedAtUtc = now;
        producer.LastError = "producer-failed";
        var consumer = AddJob(execution, source, 1, ProcessingGraphNodeFailurePolicy.Required, now);
        var dependency = new CentralDerivativeJobDependency
        {
            Execution = execution,
            ExecutionId = execution.Id,
            ConsumerJob = consumer,
            ConsumerJobId = consumer.Id,
            ProducerJob = producer,
            ProducerJobId = producer.Id,
            ProducerOutputOrdinal = 0,
            ConsumerInputOrdinal = 0,
            ConsumerBindingName = "input",
            ConsumerBindingKind = ProcessingGraphInputBindingKind.PrimaryArtifact,
            Kind = ProcessingGraphDependencyKind.Artifact,
            Required = true
        };
        execution.Dependencies.Add(dependency);
        consumer.Dependencies.Add(dependency);
        context.AddRange(source.Frame!, source, execution);
        await context.SaveChangesAsync().ConfigureAwait(false);
        await SealAsync(context, execution, now).ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);

        await CreateScheduler(context, telemetry, catalogTelemetry).ConvergeAsync(
            execution.Id, now.AddSeconds(1), CancellationToken.None).ConfigureAwait(false);

        var persistedConsumer = await context.CentralDerivativeJobs.AsNoTracking()
            .SingleAsync(item => item.Id == consumer.Id).ConfigureAwait(false);
        Assert.AreEqual(CentralDerivativeJobStatus.TerminalFailure, persistedConsumer.Status);
        Assert.AreEqual("processing.graph.required-predecessor-failed", persistedConsumer.LastError);
        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Failed,
            await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Where(item => item.Id == execution.Id)
                .Select(item => item.Status)
                .SingleAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ConvergeCancelsEveryNonLeasedNodeForExecutionCancellation()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var source = CreateArtifact();
        var execution = CreateExecution(source, now, expectedNodeCount: 1, expectedDependencyCount: 0);
        _ = AddSource(execution, source, now);
        var job = AddJob(execution, source, 0, ProcessingGraphNodeFailurePolicy.Required, now);
        context.AddRange(source.Frame!, source, execution);
        await context.SaveChangesAsync().ConfigureAwait(false);
        await SealAsync(context, execution, now).ConfigureAwait(false);
        var persistedExecution = await context.CentralProcessingGraphExecutions.SingleAsync(item =>
            item.Id == execution.Id).ConfigureAwait(false);
        persistedExecution.Status = CentralProcessingGraphExecutionStatus.CancelRequested;
        persistedExecution.CancellationRequestedAtUtc = now.AddSeconds(1);
        persistedExecution.UpdatedAtUtc = now.AddSeconds(1);
        await context.SaveChangesAsync().ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);

        await CreateScheduler(context, telemetry, catalogTelemetry).ConvergeAsync(
            execution.Id, now.AddSeconds(2), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CentralDerivativeJobStatus.Canceled,
            await context.CentralDerivativeJobs.AsNoTracking().Where(item => item.Id == job.Id)
                .Select(item => item.Status).SingleAsync().ConfigureAwait(false));
        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Canceled,
            await context.CentralProcessingGraphExecutions.AsNoTracking().Where(item => item.Id == execution.Id)
                .Select(item => item.Status).SingleAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ConvergePreservesActiveLeaseThenCancelsExpiredAttemptAndTransientSlots()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var source = CreateArtifact();
        var execution = CreateExecution(source, now, expectedNodeCount: 1, expectedDependencyCount: 0);
        _ = AddSource(execution, source, now);
        var job = AddJob(execution, source, 0, ProcessingGraphNodeFailurePolicy.Required, now);
        job.Status = CentralDerivativeJobStatus.Leased;
        job.AttemptCount = 1;
        job.LeaseOwner = "worker";
        job.LeaseToken = Guid.NewGuid();
        job.LeaseAcquiredAtUtc = now;
        job.LeaseExpiresAtUtc = now.AddMinutes(1);
        job.CancellationRequestedAtUtc = now.AddSeconds(1);
        job.CancellationRequestedBy = "operator";
        job.Attempts.Add(new CentralDerivativeJobAttempt
        {
            CentralDerivativeJobId = job.Id,
            AttemptNumber = 1,
            WorkerId = job.LeaseOwner,
            LeaseAcquiredAtUtc = now,
            LeaseExpiresAtUtc = job.LeaseExpiresAtUtc.Value,
            Outcome = CentralDerivativeAttemptOutcome.Leased
        });
        var validation = new CentralTransientValidationJob
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            AgentId = source.Frame!.AgentId,
            SubmissionSchemaVersion = "test-v1",
            SubmissionIdentitySha256 = new string('A', 64),
            CreatedAtUtc = now
        };
        validation.IdentitySlots.Add(new CentralTransientValidationIdentitySlot
        {
            ValidationJob = validation,
            CentralDerivativeJobId = job.Id,
            AgentId = source.Frame.AgentId,
            Ordinal = 0,
            State = CentralTransientValidationIdentitySlotState.Reserved,
            SubmittedEventId = Guid.NewGuid(),
            CandidateId = Guid.NewGuid(),
            ObservationId = Guid.NewGuid(),
            AssessmentId = Guid.NewGuid()
        });
        context.AddRange(source.Frame!, source, execution);
        context.Add(validation);
        await context.SaveChangesAsync().ConfigureAwait(false);
        await SealAsync(context, execution, now).ConfigureAwait(false);
        var persistedExecution = await context.CentralProcessingGraphExecutions.SingleAsync(item =>
            item.Id == execution.Id).ConfigureAwait(false);
        persistedExecution.Status = CentralProcessingGraphExecutionStatus.CancelRequested;
        persistedExecution.CancellationRequestedAtUtc = now.AddSeconds(1);
        persistedExecution.UpdatedAtUtc = now.AddSeconds(1);
        await context.SaveChangesAsync().ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var scheduler = CreateScheduler(context, telemetry, catalogTelemetry);

        await scheduler.ConvergeAsync(execution.Id, now.AddSeconds(2), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CentralDerivativeJobStatus.Leased,
            await context.CentralDerivativeJobs.AsNoTracking().Where(item => item.Id == job.Id)
                .Select(item => item.Status).SingleAsync().ConfigureAwait(false));
        Assert.AreEqual(CentralProcessingGraphExecutionStatus.CancelRequested,
            await context.CentralProcessingGraphExecutions.AsNoTracking().Where(item => item.Id == execution.Id)
                .Select(item => item.Status).SingleAsync().ConfigureAwait(false));

        await scheduler.ConvergeAsync(execution.Id, now.AddMinutes(2), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Canceled,
            await context.CentralProcessingGraphExecutions.AsNoTracking().Where(item => item.Id == execution.Id)
                .Select(item => item.Status).SingleAsync().ConfigureAwait(false));
        var canceled = await context.CentralDerivativeJobs.AsNoTracking().Include(item => item.Attempts)
            .SingleAsync(item => item.Id == job.Id).ConfigureAwait(false);
        Assert.AreEqual(CentralDerivativeJobStatus.Canceled, canceled.Status);
        Assert.AreEqual("operator", canceled.CancellationRequestedBy);
        Assert.AreEqual(now.AddSeconds(1), canceled.CancellationRequestedAtUtc);
        Assert.AreEqual(CentralDerivativeAttemptOutcome.Canceled, canceled.Attempts.Single().Outcome);
        Assert.AreEqual(CentralTransientValidationIdentitySlotState.Unused,
            await context.CentralTransientValidationIdentitySlots.AsNoTracking()
                .Where(item => item.CentralDerivativeJobId == job.Id)
                .Select(item => item.State)
                .SingleAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ConvergeBatchRecoversEveryActiveExpandedExecution()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var executionIds = new List<Guid>();
        for (var index = 0; index < 2; index++)
        {
            var executionNow = now.AddSeconds(index);
            var source = CreateArtifact();
            var execution = CreateExecution(source, executionNow, expectedNodeCount: 1,
                expectedDependencyCount: 0);
            _ = AddSource(execution, source, now);
            var job = AddJob(execution, source, 0, ProcessingGraphNodeFailurePolicy.Required, now);
            job.Status = CentralDerivativeJobStatus.Completed;
            job.CompletedAtUtc = now;
            context.AddRange(source.Frame!, source, execution);
            await context.SaveChangesAsync().ConfigureAwait(false);
            await SealAsync(context, execution, executionNow).ConfigureAwait(false);
            executionIds.Add(execution.Id);
            context.ChangeTracker.Clear();
        }
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);

        await CreateScheduler(context, telemetry, catalogTelemetry).ConvergeBatchAsync(
            now.AddMinutes(1), CancellationToken.None).ConfigureAwait(false);

        var statuses = await context.CentralProcessingGraphExecutions.AsNoTracking()
            .Where(item => executionIds.Contains(item.Id))
            .Select(item => item.Status)
            .ToArrayAsync().ConfigureAwait(false);
        Assert.HasCount(2, statuses);
        Assert.IsTrue(statuses.All(status => status == CentralProcessingGraphExecutionStatus.Completed));
    }

    [TestMethod]
    public async Task ConvergeBatchRotatesNonProgressingExecutionsBeyondBoundedBatch()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var executionIds = new List<Guid>();
        for (var index = 0; index < 101; index++)
        {
            var created = now.AddSeconds(index);
            var source = CreateArtifact();
            var execution = CreateExecution(source, created, expectedNodeCount: 1, expectedDependencyCount: 0);
            _ = AddSource(execution, source, created);
            var job = AddJob(execution, source, 0, ProcessingGraphNodeFailurePolicy.Required, created);
            job.WaitKind = CentralDerivativeWaitKind.Window;
            context.AddRange(source.Frame!, source, execution);
            await context.SaveChangesAsync().ConfigureAwait(false);
            await SealAsync(context, execution, created).ConfigureAwait(false);
            executionIds.Add(execution.Id);
            context.ChangeTracker.Clear();
        }
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var scheduler = CreateScheduler(context, telemetry, catalogTelemetry);

        await scheduler.ConvergeBatchAsync(now.AddHours(1), CancellationToken.None).ConfigureAwait(false);
        await scheduler.ConvergeBatchAsync(now.AddHours(1).AddSeconds(1), CancellationToken.None).ConfigureAwait(false);

        var finalExecutionId = executionIds[^1];
        Assert.AreEqual(now.AddHours(1).AddSeconds(1),
            await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Where(item => item.Id == finalExecutionId)
                .Select(item => item.UpdatedAtUtc)
                .SingleAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ConvergeBatchRotatesFailingExecutionSoLaterExecutionsAreRecovered()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;

        // Oldest execution: a canonical dependency whose bound artifact exceeds the bounded payload limit makes
        // convergence throw every time. Without rotation it would pin the head of the recovery batch forever.
        var corruptSource = CreateArtifact();
        var oversized = new CentralArtifact
        {
            CentralFrameId = corruptSource.Frame!.Id,
            Frame = corruptSource.Frame,
            DevicePublicId = corruptSource.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Metadata,
            Variant = "canonical",
            RecipeVersion = "metadata-v1",
            ManifestSchemaVersion = "manifest-v1",
            MediaType = "application/json",
            ByteLength = ProcessingGraphJson.MaximumDocumentBytes + 1,
            ChecksumSha256 = new string('9', 64),
            StorageReference = $"s3://skymonitor-artifacts/{Guid.NewGuid():N}",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReceivedAtUtc = now,
            CreatedUtc = now,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        var corrupt = CreateExecution(corruptSource, now.AddMinutes(-10), expectedNodeCount: 2, expectedDependencyCount: 1);
        corrupt.ExpectedOutputCount = 1;
        _ = AddSource(corrupt, corruptSource, now);
        var producer = AddJob(corrupt, corruptSource, 0, ProcessingGraphNodeFailurePolicy.Required, now);
        producer.Status = CentralDerivativeJobStatus.Completed;
        producer.CompletedAtUtc = now;
        var output = CentralDerivativeJobOutput.CreateFromFrozenPlan(
            producer, 0,
            new ProcessingGraphProductContract(FrameArtifactRole.Metadata, "canonical", ProcessingProductKind.Metadata));
        producer.Outputs.Add(output);
        var consumer = AddJob(corrupt, corruptSource, 1, ProcessingGraphNodeFailurePolicy.Required, now);
        var dependency = new CentralDerivativeJobDependency
        {
            Execution = corrupt,
            ExecutionId = corrupt.Id,
            ConsumerJob = consumer,
            ConsumerJobId = consumer.Id,
            ProducerJob = producer,
            ProducerJobId = producer.Id,
            ProducerOutputOrdinal = 0,
            ConsumerInputOrdinal = 0,
            ConsumerBindingName = "metadata",
            ConsumerBindingKind = ProcessingGraphInputBindingKind.CanonicalJson,
            Kind = ProcessingGraphDependencyKind.CanonicalJson,
            Required = true
        };
        var requirement = new CentralDerivativeJobInputRequirement
        {
            Job = consumer,
            CentralDerivativeJobId = consumer.Id,
            BindingName = "metadata",
            GraphDependency = dependency,
            GraphDependencyId = dependency.Id,
            GraphInputOrdinal = 0,
            GraphInputBindingKind = ProcessingGraphInputBindingKind.CanonicalJson,
            SourceKind = CentralDerivativeInputSourceKind.Canonical,
            IsRequired = true,
            SelectorJson = "{}",
            ExpectedAgentId = corruptSource.Frame.AgentId,
            ExpectedRigId = corruptSource.Frame.RigId,
            ResolutionState = CentralDerivativeInputResolutionState.Waiting
        };
        dependency.InputRequirements.Add(requirement);
        consumer.Dependencies.Add(dependency);
        consumer.InputRequirements.Add(requirement);
        corrupt.Dependencies.Add(dependency);
        context.AddRange(corruptSource.Frame, corruptSource, oversized, corrupt);
        await context.SaveChangesAsync().ConfigureAwait(false);
        output.ResultCentralArtifactId = oversized.Id;
        output.ResultArtifact = oversized;
        output.ResultOutputIdentitySha256 = new string('A', 64);
        output.BoundAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        await SealAsync(context, corrupt, now.AddMinutes(-10)).ConfigureAwait(false);

        // Newer execution: completes on convergence.
        var healthySource = CreateArtifact();
        var healthy = CreateExecution(healthySource, now, expectedNodeCount: 1, expectedDependencyCount: 0);
        _ = AddSource(healthy, healthySource, now);
        var healthyJob = AddJob(healthy, healthySource, 0, ProcessingGraphNodeFailurePolicy.Required, now);
        healthyJob.Status = CentralDerivativeJobStatus.Completed;
        healthyJob.CompletedAtUtc = now;
        context.AddRange(healthySource.Frame!, healthySource, healthy);
        await context.SaveChangesAsync().ConfigureAwait(false);
        await SealAsync(context, healthy, now).ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var scheduler = CreateScheduler(context, telemetry, catalogTelemetry);

        var firstPass = now.AddMinutes(1);
        await scheduler.ConvergeBatchAsync(firstPass, CancellationToken.None).ConfigureAwait(false);

        var corruptAfterFirstPass = await context.CentralProcessingGraphExecutions.AsNoTracking()
            .SingleAsync(item => item.Id == corrupt.Id).ConfigureAwait(false);
        // The failing execution must stay nonterminal. On SQL Server the convergence transaction rolls the durable
        // Pending -> Running step back to Pending; the InMemory provider ignores transactions, so the intermediate
        // Running row survives here and only the nonterminal outcome is provider-neutral.
        Assert.IsTrue(corruptAfterFirstPass.Status is CentralProcessingGraphExecutionStatus.Pending or
            CentralProcessingGraphExecutionStatus.Running, $"unexpected status {corruptAfterFirstPass.Status}");
        Assert.IsNull(corruptAfterFirstPass.CompletedAtUtc);
        Assert.AreEqual(firstPass, corruptAfterFirstPass.UpdatedAtUtc,
            "a failing execution must rotate behind newer work instead of pinning the oldest recovery slot");
        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Completed,
            await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Where(item => item.Id == healthy.Id).Select(item => item.Status).SingleAsync().ConfigureAwait(false));

        // A second pass keeps rotating the failing execution rather than leaving its slot frozen.
        var secondPass = firstPass.AddMinutes(1);
        await scheduler.ConvergeBatchAsync(secondPass, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(secondPass,
            await context.CentralProcessingGraphExecutions.AsNoTracking()
                .Where(item => item.Id == corrupt.Id).Select(item => item.UpdatedAtUtc).SingleAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ConvergeClassifiesEveryTerminalNodeOutcome()
    {
        var cases = new[]
        {
            (CentralDerivativeJobStatus.Completed, ProcessingGraphNodeFailurePolicy.Required,
                CentralProcessingGraphExecutionStatus.Completed),
            (CentralDerivativeJobStatus.Skipped, ProcessingGraphNodeFailurePolicy.Required,
                CentralProcessingGraphExecutionStatus.Completed),
            (CentralDerivativeJobStatus.TerminalFailure, ProcessingGraphNodeFailurePolicy.Required,
                CentralProcessingGraphExecutionStatus.Failed),
            (CentralDerivativeJobStatus.Canceled, ProcessingGraphNodeFailurePolicy.Required,
                CentralProcessingGraphExecutionStatus.Failed),
            (CentralDerivativeJobStatus.Quarantined, ProcessingGraphNodeFailurePolicy.Optional,
                CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures),
            (CentralDerivativeJobStatus.Superseded, ProcessingGraphNodeFailurePolicy.Optional,
                CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures)
        };
        foreach (var (jobStatus, failurePolicy, expectedStatus) in cases)
        {
            await using var context = CreateContext();
            var now = DateTimeOffset.UtcNow;
            var source = CreateArtifact();
            var execution = CreateExecution(source, now, expectedNodeCount: 1, expectedDependencyCount: 0);
            _ = AddSource(execution, source, now);
            var job = AddJob(execution, source, 0, failurePolicy, now);
            job.Status = jobStatus;
            job.CompletedAtUtc = now;
            if (jobStatus == CentralDerivativeJobStatus.TerminalFailure)
            {
                job.LastError = "failed";
                job.LastFailedAtUtc = now;
            }
            context.AddRange(source.Frame!, source, execution);
            await context.SaveChangesAsync().ConfigureAwait(false);
            await SealAsync(context, execution, now).ConfigureAwait(false);
            using var telemetry = new CentralDerivativeWorkerTelemetry();
            using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);

            await CreateScheduler(context, telemetry, catalogTelemetry).ConvergeAsync(
                execution.Id, now.AddSeconds(1), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(expectedStatus,
                await context.CentralProcessingGraphExecutions.AsNoTracking().Where(item => item.Id == execution.Id)
                    .Select(item => item.Status).SingleAsync().ConfigureAwait(false));
        }
    }

    [TestMethod]
    public async Task ConvergeFromPendingRecordsStartBeforeTerminalizingAndStaysIdempotent()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var source = CreateArtifact();
        var execution = CreateExecution(source, now, expectedNodeCount: 1, expectedDependencyCount: 0);
        _ = AddSource(execution, source, now);
        var job = AddJob(execution, source, 0, ProcessingGraphNodeFailurePolicy.Required, now);
        job.Status = CentralDerivativeJobStatus.Completed;
        job.CompletedAtUtc = now;
        context.AddRange(source.Frame!, source, execution);
        await context.SaveChangesAsync().ConfigureAwait(false);
        await SealAsync(context, execution, now).ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);
        var scheduler = CreateScheduler(context, telemetry, catalogTelemetry);
        var converged = now.AddSeconds(1);

        // The execution never entered Running before all nodes became terminal: convergence must record the legal
        // Pending -> Running -> Completed lifecycle rather than a single Pending -> Completed save.
        await scheduler.ConvergeAsync(execution.Id, converged, CancellationToken.None).ConfigureAwait(false);

        var completed = await context.CentralProcessingGraphExecutions.AsNoTracking()
            .SingleAsync(item => item.Id == execution.Id).ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Completed, completed.Status);
        Assert.AreEqual(converged, completed.StartedAtUtc);
        Assert.AreEqual(converged, completed.CompletedAtUtc);
        Assert.AreEqual(converged, completed.UpdatedAtUtc);

        // Replaying convergence (restart recovery, duplicate signal) against the terminal execution is a no-op.
        await scheduler.ConvergeAsync(execution.Id, converged.AddMinutes(1), CancellationToken.None).ConfigureAwait(false);
        await scheduler.ConvergeBatchAsync(converged.AddMinutes(2), CancellationToken.None).ConfigureAwait(false);
        var replayed = await context.CentralProcessingGraphExecutions.AsNoTracking()
            .SingleAsync(item => item.Id == execution.Id).ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Completed, replayed.Status);
        Assert.AreEqual(converged, replayed.StartedAtUtc);
        Assert.AreEqual(converged, replayed.CompletedAtUtc);
        Assert.AreEqual(converged, replayed.UpdatedAtUtc);
    }

    [TestMethod]
    public async Task ConvergeFromPendingSkipsNoInputNodeAndCompletesInFirstPass()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var source = CreateArtifact();
        var execution = CreateExecution(source, now, expectedNodeCount: 1, expectedDependencyCount: 0);
        _ = AddSource(execution, source, now);
        // A waiting node with no dependencies and no inputs terminalizes as Skipped during the same convergence pass
        // that first observes the Pending execution.
        _ = AddJob(execution, source, 0, ProcessingGraphNodeFailurePolicy.Required, now);
        context.AddRange(source.Frame!, source, execution);
        await context.SaveChangesAsync().ConfigureAwait(false);
        await SealAsync(context, execution, now).ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);

        await CreateScheduler(context, telemetry, catalogTelemetry).ConvergeAsync(
            execution.Id, now.AddSeconds(1), CancellationToken.None).ConfigureAwait(false);

        var completed = await context.CentralProcessingGraphExecutions.AsNoTracking().Include(item => item.Jobs)
            .SingleAsync(item => item.Id == execution.Id).ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Completed, completed.Status);
        Assert.AreEqual(now.AddSeconds(1), completed.StartedAtUtc);
        Assert.AreEqual(CentralDerivativeJobStatus.Skipped, completed.Jobs.Single().Status);
        Assert.AreEqual("processing.graph.no-input", completed.Jobs.Single().StateReasonCode);
    }

    [TestMethod]
    public async Task ConvergeFailedBeforeStartTransitionsThroughRunning()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var source = CreateArtifact();
        var execution = CreateExecution(source, now, expectedNodeCount: 2, expectedDependencyCount: 0);
        _ = AddSource(execution, source, now);
        var failed = AddJob(execution, source, 0, ProcessingGraphNodeFailurePolicy.Required, now);
        failed.Status = CentralDerivativeJobStatus.TerminalFailure;
        failed.LastError = "failed";
        failed.LastFailedAtUtc = now;
        failed.CompletedAtUtc = now;
        var consumer = AddJob(execution, source, 1, ProcessingGraphNodeFailurePolicy.Required, now);
        var outcome = new CentralDerivativeJobDependency
        {
            Execution = execution,
            ExecutionId = execution.Id,
            ConsumerJob = consumer,
            ConsumerJobId = consumer.Id,
            ProducerJob = failed,
            ProducerJobId = failed.Id,
            ProducerOutputOrdinal = 0,
            ConsumerInputOrdinal = 0,
            ConsumerBindingName = "after",
            ConsumerBindingKind = ProcessingGraphInputBindingKind.PrimaryArtifact,
            Kind = ProcessingGraphDependencyKind.Outcome,
            Required = true
        };
        execution.ExpectedDependencyCount = 1;
        execution.Dependencies.Add(outcome);
        consumer.Dependencies.Add(outcome);
        context.AddRange(source.Frame!, source, execution);
        await context.SaveChangesAsync().ConfigureAwait(false);
        await SealAsync(context, execution, now).ConfigureAwait(false);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);

        await CreateScheduler(context, telemetry, catalogTelemetry).ConvergeAsync(
            execution.Id, now.AddSeconds(1), CancellationToken.None).ConfigureAwait(false);

        var result = await context.CentralProcessingGraphExecutions.AsNoTracking().Include(item => item.Jobs)
            .SingleAsync(item => item.Id == execution.Id).ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Failed, result.Status);
        Assert.AreEqual(now.AddSeconds(1), result.StartedAtUtc);
        Assert.AreEqual(now.AddSeconds(1), result.CompletedAtUtc);
        Assert.AreEqual(CentralDerivativeJobStatus.TerminalFailure,
            result.Jobs.Single(item => item.GraphNodeOrdinal == 1).Status);
    }

    [TestMethod]
    public async Task ConvergeCanceledBeforeStartNeverEntersRunning()
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var source = CreateArtifact();
        var execution = CreateExecution(source, now, expectedNodeCount: 1, expectedDependencyCount: 0);
        _ = AddSource(execution, source, now);
        var job = AddJob(execution, source, 0, ProcessingGraphNodeFailurePolicy.Required, now);
        job.WaitKind = CentralDerivativeWaitKind.Window;
        context.AddRange(source.Frame!, source, execution);
        await context.SaveChangesAsync().ConfigureAwait(false);
        await SealAsync(context, execution, now).ConfigureAwait(false);
        var pending = await context.CentralProcessingGraphExecutions.SingleAsync(item => item.Id == execution.Id)
            .ConfigureAwait(false);
        pending.Status = CentralProcessingGraphExecutionStatus.CancelRequested;
        pending.CancellationRequestedAtUtc = now.AddSeconds(1);
        pending.UpdatedAtUtc = now.AddSeconds(1);
        await context.SaveChangesAsync().ConfigureAwait(false);
        context.ChangeTracker.Clear();
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        using var catalogTelemetry = new ProcessingGraphCatalogTelemetry(TimeProvider.System);

        await CreateScheduler(context, telemetry, catalogTelemetry).ConvergeAsync(
            execution.Id, now.AddSeconds(2), CancellationToken.None).ConfigureAwait(false);

        var canceled = await context.CentralProcessingGraphExecutions.AsNoTracking().Include(item => item.Jobs)
            .SingleAsync(item => item.Id == execution.Id).ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Canceled, canceled.Status);
        Assert.IsNull(canceled.StartedAtUtc, "an execution canceled before it started must not claim a start time");
        Assert.AreEqual(now.AddSeconds(1), canceled.CancellationRequestedAtUtc);
        Assert.AreEqual(now.AddSeconds(2), canceled.CompletedAtUtc);
        Assert.AreEqual(CentralDerivativeJobStatus.Canceled, canceled.Jobs.Single().Status);
    }

    private static EnvironmentalObservationV1 CreateObservation(
        EnvironmentalObservationKind kind,
        double? numericValue = null,
        bool? booleanValue = null)
    {
        var now = DateTimeOffset.UtcNow;
        var parameters = CaptureContractJson.SerializeToElement(new { });
        return new(
            EnvironmentalObservationV1.CurrentSchemaVersion,
            Guid.NewGuid(),
            new EnvironmentalObservationTarget(Guid.NewGuid()),
            new EnvironmentalObservationSource(
                "test",
                "test",
                "1",
                EnvironmentalObservationSourceKind.Measured,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("test", "1"),
                    parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            now,
            null,
            null,
            now,
            now.AddMinutes(1),
            now.AddMinutes(2),
            new EnvironmentalObservationValue(
                kind,
                kind == EnvironmentalObservationKind.RainState
                    ? EnvironmentalObservationUnit.Boolean
                    : kind == EnvironmentalObservationKind.PrecipitationRate
                        ? EnvironmentalObservationUnit.MillimetersPerHour
                        : EnvironmentalObservationUnit.DegreesCelsius,
                numericValue,
                booleanValue,
                EnvironmentalObservationQuality.Good),
            []);
    }

    private static CentralProcessingGraphScheduler CreateScheduler(
        ApplicationDbContext context,
        CentralDerivativeWorkerTelemetry telemetry,
        ProcessingGraphCatalogTelemetry catalogTelemetry,
        ICentralDerivativeWindowResolver? windowResolver = null,
        ICentralProcessingGraphNodeRegistry? nodeRegistry = null,
        IEnvironmentalObservationQueryService? environmentalQuery = null,
        ICentralArtifactObjectReader? objectReader = null)
    {
        var recipeCatalog = new CentralDerivativeRecipeCatalog();
        var registry = nodeRegistry ?? new CentralProcessingGraphNodeRegistry(recipeCatalog);
        var catalog = new ProcessingGraphCatalogService(
            context, registry, TimeProvider.System, catalogTelemetry, NullLogger<ProcessingGraphCatalogService>.Instance);
        return new(
            context,
            catalog,
            registry,
            windowResolver ?? new NoopWindowResolver(),
            objectReader ?? new UnusedObjectReader(),
            telemetry,
            TimeProvider.System,
            environmentalQuery);
    }

    private static ApplicationDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static ProcessingGraphDefinition CreatePreviewGraph(bool withWindow = false)
    {
        _ = BuiltInProcessingRecipes.TryGetDefinition(
            BuiltInProcessingRecipes.EncodedPreview, out var recipeDefinition);
        var options = BuiltInProcessingRecipes.NormalizeOptions(
            BuiltInProcessingRecipes.EncodedPreview,
            CaptureContractJson.SerializeToElement(new EncodedPreviewOptions()));
        var outputRecipe = withWindow
            ? recipeDefinition! with { OperationKind = ProcessingOperationKind.Window }
            : recipeDefinition;
        return new(
            ProcessingGraphSchemaVersions.Current,
            "test-preview",
            "1",
            [new ProcessingGraphSourceDefinition(
                "$raw",
                [new ProcessingGraphProductContract(
                    FrameArtifactRole.Raw, "source", ProcessingProductKind.PixelData)])],
            [new ProcessingGraphNodeDefinition(
                "Preview",
                BuiltInProcessingRecipes.EncodedPreview,
                CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
                withWindow ? ProcessingOperationKind.Window : ProcessingOperationKind.Transform,
                true,
                ProcessingGraphNodeFailurePolicy.Required,
                0,
                options,
                [new ProcessingGraphDependencyDefinition("$raw")],
                [new ProcessingGraphInputContract(
                    [FrameArtifactRole.Raw],
                    [ProcessingProductKind.PixelData],
                    [],
                    [],
                    [])],
                [new ProcessingGraphProductContract(
                    FrameArtifactRole.Preview,
                    CentralDerivativeRecipeCatalog.PreviewVariant,
                    ProcessingProductKind.PixelData,
                    outputRecipe,
                    MediaType: "image/jpeg")],
                withWindow
                    ? new ProcessingGraphWindowRequirement(
                        ProcessingGraphWindowKind.Centered,
                        5,
                        5,
                        [-2, -1, 0, 1, 2],
                        ["rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"])
                    : null,
                ImmutableArray<string>.Empty,
                [ProcessingGraphHosts.LogicHost])]);
    }

    /// <summary>Adds a metadata source consumed through a CanonicalJson binding; portable compilation accepts it.</summary>
    private static ProcessingGraphDefinition WithCanonicalJsonBinding(ProcessingGraphDefinition baseline)
    {
        var node = baseline.Nodes[0];
        return baseline with
        {
            Sources =
            [
                .. baseline.Sources,
                new ProcessingGraphSourceDefinition("$metadata",
                    [new ProcessingGraphProductContract(
                        FrameArtifactRole.Metadata, "canonical", ProcessingProductKind.Metadata)])
            ],
            Nodes = [new ProcessingGraphNodeDefinition(
                node.Id, node.StepAlias, node.StepVersion, node.OperationKind, node.Enabled,
                node.FailurePolicy, node.Order, node.EffectiveOptions,
                [
                    .. node.Dependencies,
                    new ProcessingGraphDependencyDefinition("$metadata", ProcessingGraphDependencyKind.CanonicalJson)
                ],
                [
                    .. node.Inputs,
                    new ProcessingGraphInputContract(
                        [FrameArtifactRole.Metadata],
                        [ProcessingProductKind.Metadata],
                        [],
                        [],
                        [],
                        BindingName: "canonical",
                        BindingKind: ProcessingGraphInputBindingKind.CanonicalJson)
                ],
                node.Outputs, node.Window, node.CapabilityLabels, node.HostApplicability)]
        };
    }

    private static CentralDerivativeJobLease CreateGraphLease(ProcessingGraphExecutionPlan plan)
    {
        var node = plan.Nodes.Single();
        return new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "worker",
            DateTimeOffset.UtcNow.AddMinutes(1),
            Guid.NewGuid(),
            Guid.NewGuid(),
            FrameArtifactRole.Raw,
            "raw-v1",
            "/content",
            new string('A', 64),
            "application/octet-stream",
            Guid.NewGuid(),
            "agent",
            DateTimeOffset.UtcNow,
            null,
            null,
            FrameArtifactRole.Preview,
            CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
            CentralDerivativeRecipeCatalog.PreviewVariant,
            BuiltInProcessingRecipes.EncodedPreview,
            "{}",
            "{}",
            new string('B', 64),
            new string('C', 64),
            null,
            null,
            1,
            3,
            GraphExecutionId: Guid.NewGuid(),
            GraphRevisionId: Guid.NewGuid(),
            GraphNodeId: node.Definition.Id,
            GraphNodeOrdinal: 0,
            SharedNodePlanIdentitySha256: node.IdentitySha256,
            FrozenNodePlanJson: LogicHostProcessingGraphAdapter.FreezeNode(node),
            CentralPlanIdentitySha256: plan.PlanIdentitySha256,
            FrozenCentralPlanJson: LogicHostProcessingGraphAdapter.FreezePlan(plan),
            GraphDefinitionIdentitySha256: plan.DefinitionIdentitySha256,
            FrozenDefinitionJson: Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(CreatePreviewGraph())));
    }

    private static async Task SealAsync(
        ApplicationDbContext context,
        CentralProcessingGraphExecution execution,
        DateTimeOffset now)
    {
        execution.ExpandedAtUtc = now;
        execution.UpdatedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        context.ChangeTracker.Clear();
    }

    private static CentralArtifact CreateArtifact()
    {
        var frame = new CentralFrame
        {
            Id = Guid.NewGuid(),
            FrameId = Guid.NewGuid(),
            DevicePublicId = Guid.NewGuid(),
            AgentId = "agent",
            RigId = "rig",
            CapturedAtUtc = DateTimeOffset.UtcNow
        };
        return new()
        {
            CentralFrameId = frame.Id,
            Frame = frame,
            DevicePublicId = frame.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Raw,
            Variant = "source",
            RecipeVersion = "raw-v1",
            ManifestSchemaVersion = "manifest-v1",
            MediaType = "application/octet-stream",
            ByteLength = 1,
            ChecksumSha256 = new string('A', 64),
            StorageReference = $"s3://skymonitor-artifacts/{Guid.NewGuid():N}",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReceivedAtUtc = frame.CapturedAtUtc,
            CreatedUtc = frame.CapturedAtUtc,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
    }

    private static CentralProcessingGraphExecution CreateExecution(
        CentralArtifact source,
        DateTimeOffset now,
        int expectedNodeCount,
        int expectedDependencyCount)
        => new()
        {
            ExecutionClass = CentralProcessingGraphExecutionClass.Replay,
            Status = CentralProcessingGraphExecutionStatus.Pending,
            RequestIdentitySha256 = new string('B', 64),
            RevisionId = Guid.NewGuid(),
            DefinitionIdentitySha256 = new string('C', 64),
            FrozenDefinitionJson = "{}",
            CentralPlanIdentitySha256 = new string('D', 64),
            FrozenCentralPlanJson = "{}",
            ExpectedSourceCount = 1,
            ExpectedNodeCount = expectedNodeCount,
            ExpectedDependencyCount = expectedDependencyCount,
            ExpectedOutputCount = 0,
            ObservatoryId = Guid.NewGuid(),
            LogicalCameraId = Guid.NewGuid(),
            LogicalCameraInstallationId = Guid.NewGuid(),
            InstallationPublicId = Guid.NewGuid(),
            AnchorSourceCentralArtifactId = source.Id,
            AnchorSourceArtifactId = source.ArtifactId,
            AnchorSourceChecksumSha256 = source.ChecksumSha256,
            Trigger = CentralProcessingGraphTrigger.Replay,
            ActorId = "operator",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReasonCode = "test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

    private static CentralProcessingGraphExecutionSource AddSource(
        CentralProcessingGraphExecution execution,
        CentralArtifact artifact,
        DateTimeOffset now)
    {
        var source = new CentralProcessingGraphExecutionSource
        {
            Execution = execution,
            ExecutionId = execution.Id,
            SourceId = "$raw",
            CentralArtifactId = artifact.Id,
            Artifact = artifact,
            ArtifactId = artifact.ArtifactId,
            ArtifactChecksumSha256 = artifact.ChecksumSha256,
            ArtifactByteLength = artifact.ByteLength,
            SelectionEvidenceJson = "{}",
            SelectionEvidenceSha256 = new string('E', 64),
            SelectedAtUtc = now
        };
        execution.Sources.Add(source);
        return source;
    }

    private static CentralDerivativeJob AddJob(
        CentralProcessingGraphExecution execution,
        CentralArtifact source,
        int ordinal,
        ProcessingGraphNodeFailurePolicy failurePolicy,
        DateTimeOffset now)
    {
        var job = new CentralDerivativeJob
        {
            GraphExecution = execution,
            GraphExecutionId = execution.Id,
            GraphNodeId = $"node-{ordinal}",
            GraphNodeOrdinal = ordinal,
            SharedNodePlanIdentitySha256 = new string('F', 64),
            FrozenNodePlanJson = "{}",
            GraphFailurePolicy = failurePolicy,
            SourceCentralArtifactId = source.Id,
            SourceArtifact = source,
            TargetRole = FrameArtifactRole.Preview,
            TargetRecipeVersion = "preview-v1",
            TargetVariant = $"preview-{ordinal}",
            RecipeName = BuiltInProcessingRecipes.EncodedPreview,
            RecipeOptionsJson = "{}",
            InputSelectorJson = "{}",
            RequestedRecipeIdentitySha256 = new string('1', 64),
            ExpectedRecipeIdentitySha256 = new string('1', 64),
            RequestIdentitySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"job-{ordinal}"))),
            Status = CentralDerivativeJobStatus.Waiting,
            WaitKind = CentralDerivativeWaitKind.Dependencies,
            ResolutionStartedAtUtc = now,
            StateReasonCode = "processing.graph.waiting-dependencies",
            MaxAttempts = 3,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        execution.Jobs.Add(job);
        return job;
    }

    private static void AddDependency(
        CentralProcessingGraphExecution execution,
        CentralProcessingGraphExecutionSource source,
        CentralDerivativeJob consumer,
        DateTimeOffset now)
    {
        var dependency = new CentralDerivativeJobDependency
        {
            Execution = execution,
            ExecutionId = execution.Id,
            ConsumerJob = consumer,
            ConsumerJobId = consumer.Id,
            ProducerSource = source,
            ProducerSourceId = source.Id,
            ProducerOutputOrdinal = 0,
            ConsumerInputOrdinal = 0,
            ConsumerBindingName = "input",
            ConsumerBindingKind = ProcessingGraphInputBindingKind.PrimaryArtifact,
            Kind = ProcessingGraphDependencyKind.Artifact,
            Required = true
        };
        var requirement = new CentralDerivativeJobInputRequirement
        {
            Job = consumer,
            CentralDerivativeJobId = consumer.Id,
            BindingName = "input",
            GraphDependency = dependency,
            GraphDependencyId = dependency.Id,
            GraphInputOrdinal = 0,
            GraphInputBindingKind = ProcessingGraphInputBindingKind.PrimaryArtifact,
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            IsRequired = true,
            SelectorJson = "{}",
            ExpectedAgentId = "agent",
            ExpectedRigId = "rig",
            ResolutionState = CentralDerivativeInputResolutionState.Waiting
        };
        dependency.InputRequirements.Add(requirement);
        execution.Dependencies.Add(dependency);
        consumer.Dependencies.Add(dependency);
        consumer.InputRequirements.Add(requirement);
    }

    private sealed class NoopWindowResolver : ICentralDerivativeWindowResolver
    {
        public Task ResolveAffectedAsync(CentralArtifact artifact, DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ResolveWaitingAsync(DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ResolveAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RecordingWindowResolver : ICentralDerivativeWindowResolver
    {
        public List<Guid> ResolvedJobIds { get; } = [];

        public Task ResolveAffectedAsync(
            CentralArtifact artifact, DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ResolveWaitingAsync(DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ResolveAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            ResolvedJobIds.Add(jobId);
            return Task.CompletedTask;
        }
    }

    private sealed class WindowNodeRegistry : ICentralProcessingGraphNodeRegistry
    {
        private readonly CentralProcessingGraphNodeHandler handler;

        public WindowNodeRegistry(ICentralDerivativeRecipeCatalog recipeCatalog)
        {
            var recipe = recipeCatalog.GetRequiredRecipes(FrameArtifactRole.Raw).Single(candidate =>
                candidate.RecipeName == BuiltInProcessingRecipes.EncodedPreview);
            _ = BuiltInProcessingRecipes.TryGetDefinition(recipe.RecipeName, out var definition);
            handler = new(
                recipe.RecipeName,
                recipe.RecipeVersion,
                ProcessingOperationKind.Window,
                CentralProcessingGraphNodeHandlerKind.BuiltInRecipe,
                recipe,
                definition!);
        }

        public ImmutableArray<string> Capabilities => [];

        public CentralProcessingGraphNodeHandler GetRequired(string stepAlias)
            => stepAlias == handler.StepAlias ? handler : throw new InvalidOperationException("Unexpected step alias.");

        public bool Validate(ProcessingGraphExecutionPlan plan) => plan.Nodes.Length == 1;
    }

    private sealed class StubEnvironmentalQueryService : IEnvironmentalObservationQueryService
    {
        public Task<IReadOnlyList<ReceivedEnvironmentalObservationV1>> QueryAsync(
            EnvironmentalObservationQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EnvironmentalObservationMatch> CorrelateAsync(
            EnvironmentalObservationCorrelationRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EnvironmentalObservationMatch> CorrelateFrameAsync(
            Guid frameId,
            EnvironmentalObservationSelector selector,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EnvironmentalObservationSelection> SelectFrameAsync(
            Guid frameId,
            EnvironmentalObservationSelector selector,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new EnvironmentalObservationSelection(
                new(EnvironmentalObservationMatchStatus.Fresh, null, TimeSpan.Zero, false),
                Guid.NewGuid(),
                new string('3', 64)));
    }

    private sealed class UnusedObjectReader : ICentralArtifactObjectReader
    {
        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CanonicalObjectReader(byte[] payload) : ICentralArtifactObjectReader
    {
        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
            => Task.FromResult(new CentralArtifactObjectSnapshot("canonical", "etag", payload.Length));

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
            => destination.WriteAsync(payload, cancellationToken).AsTask();
    }
}
