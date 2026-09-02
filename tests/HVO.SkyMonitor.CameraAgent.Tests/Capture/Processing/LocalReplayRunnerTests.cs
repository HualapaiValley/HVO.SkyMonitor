using System.Net.Sockets;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Replay;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Test await-using declarations have no synchronization-context dependency.")]
public sealed class LocalReplayRunnerTests
{
    private static readonly byte[] AuthenticationKey = Encoding.UTF8.GetBytes(
        "local-replay-runner-test-key-0001");

    [TestMethod]
    public async Task UnixSocketRunner_MatchesInProcessOutputWithoutEmbeddingPayloadInMetadata()
    {
        var socketPath = CreateSocketPath();
        var options = CreateOptions(socketPath);
        using var stopping = new CancellationTokenSource();
        await using var server = new LocalReplayRunnerServer(options);
        var serverTask = server.RunAsync(stopping.Token);
        await WaitForSocketAsync(socketPath).ConfigureAwait(false);
        await using var client = new LocalReplayRunnerClient(options);
        var probedCapabilities = await client.ProbeAsync().ConfigureAwait(false);
        Assert.AreEqual(ReplayProtocol.Version, probedCapabilities.ProtocolVersion);
        var request = ProcessingConformanceFixture.CreateRequest(
            ProcessingConformanceFixture.CreateProcessingArtifact());
        var expected = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);
        var context = CreateJobContext();

        var projection = ReplayProjection.ProjectRequest(
            AuthenticationKey,
            context,
            request,
            options,
            DateTimeOffset.UtcNow);
        var metadata = ReplayProtocol.SerializeMetadata(projection.Metadata, options.MaxMetadataBytes);
        Assert.IsFalse(
            Encoding.UTF8.GetString(metadata).Contains(
                Convert.ToBase64String(ProcessingConformanceFixture.Payload.Span),
                StringComparison.Ordinal));
        CollectionAssert.AreEqual(
            ProcessingConformanceFixture.Payload.ToArray(),
            projection.Payloads.Single().ToArray());

        var actual = await client.ExecuteAsync(context, request).ConfigureAwait(false);

        Assert.AreEqual(expected.Status, actual.Status);
        Assert.AreEqual(expected.Products.Single().OutputIdentitySha256, actual.Products.Single().OutputIdentitySha256);
        Assert.AreEqual(expected.Products.Single().ChecksumSha256, actual.Products.Single().ChecksumSha256);
        CollectionAssert.AreEqual(
            expected.Products.Single().Payload.ToArray(),
            actual.Products.Single().Payload.ToArray());
        Assert.IsNotNull(client.LastCapabilities);
        Assert.AreEqual(ReplayProtocol.Version, client.LastCapabilities.ProtocolVersion);
        Assert.IsNotNull(client.LastExecutionEvidence);
        Assert.AreEqual(ProcessingConformanceFixture.Payload.Length, client.LastExecutionEvidence.RequestPayloadBytes);
        Assert.AreEqual(expected.Products.Single().Payload.Length, client.LastExecutionEvidence.ResponsePayloadBytes);
        Assert.AreEqual(1, client.LastExecutionEvidence.RequestPayloadFrames);
        Assert.AreEqual(1, client.LastExecutionEvidence.ResponsePayloadFrames);
        await StopServerAsync(stopping, serverTask).ConfigureAwait(false);
    }

    [TestMethod]
    public void ProtocolMetadata_UsesStringEnumsAndRejectsIntegerEnums()
    {
        var options = CreateOptions(CreateSocketPath());
        var capabilities = ReplayRunnerCapabilities.Create(options);
        var metadata = ReplayProtocol.SerializeMetadata(capabilities, options.MaxMetadataBytes);
        var json = Encoding.UTF8.GetString(metadata);
        StringAssert.Contains(json, "\"status\":\"NotApplicable\"", StringComparison.Ordinal);
        var invalid = Encoding.UTF8.GetBytes(json.Replace(
            "\"status\":\"NotApplicable\"",
            "\"status\":1",
            StringComparison.Ordinal));

        Assert.Throws<LocalReplayRunnerProtocolException>(() =>
            ReplayProtocol.DeserializeMetadata<ReplayRunnerCapabilities>(invalid, options.MaxMetadataBytes));
    }

    [TestMethod]
    public async Task ProtocolWrite_StopsWhenPeerDoesNotRead()
    {
        await using var stream = new BlockingWriteStream();

        await Assert.ThrowsAsync<ReplayIdleTimeoutException>(async () =>
            await ReplayProtocol.WriteFrameAsync(
                stream,
                ReplayFrameType.RequestPayload,
                0,
                new byte[1024],
                CancellationToken.None,
                TimeSpan.FromMilliseconds(100)).ConfigureAwait(false));
    }

    [TestMethod]
    public void Options_RejectTransferBudgetAboveProcessIsolationLimit()
    {
        var options = CreateOptions(
            CreateSocketPath(),
            maximumTransferBytes: LocalReplayRunnerOptions.MaximumTransferBytes + 1);
        var aggregate = CreateOptions(
            CreateSocketPath(),
            maximumTransferBytes: LocalReplayRunnerOptions.MaximumTransferBytes,
            maximumConcurrency: 3);

        Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Throws<InvalidOperationException>(() => aggregate.Validate());
    }

    [TestMethod]
    public async Task UnixSocketRunner_RemovesStaleSocketBeforeColdRestart()
    {
        var socketPath = CreateSocketPath();
        await File.WriteAllBytesAsync(socketPath, [0]).ConfigureAwait(false);
        Assert.IsTrue(Path.Exists(socketPath));

        var options = CreateOptions(socketPath);
        using var stopping = new CancellationTokenSource();
        await using var server = new LocalReplayRunnerServer(options);
        var serverTask = server.RunAsync(stopping.Token);
        await WaitForSocketAsync(socketPath).ConfigureAwait(false);
        await using var client = new LocalReplayRunnerClient(options);

        var outcome = await client.ExecuteAsync(
            CreateJobContext(),
            ProcessingConformanceFixture.CreateRequest(
                ProcessingConformanceFixture.CreateProcessingArtifact())).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        await StopServerAsync(stopping, serverTask).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Adapter_NeverDispatchesLiveExecutionToConfiguredLocalRunner()
    {
        var options = CreateOptions(CreateSocketPath());
        await using var client = new LocalReplayRunnerClient(options);
        var adapter = CreateAdapter(client);
        var context = CreateProcessingContext(ProcessingGraphExecutionClass.Live);
        context.BeginNode("preview", [], ["$raw"]);
        var request = ProcessingConformanceFixture.CreateRequest(
            ProcessingConformanceFixture.CreateProcessingArtifact());

        var outcome = await adapter.ExecuteAsync(context, request, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
    }

    [TestMethod]
    public async Task DescriptorExecutionRecordsOneCopyOfInputProvenance()
    {
        var adapter = new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var context = CreateProcessingContext(ProcessingGraphExecutionClass.Live);
        context.BeginNode("preview", [], ["$raw"]);
        var descriptorContext = new CaptureDescriptorProcessingContext(context);
        var request = ProcessingConformanceFixture.CreateRequest(
            ProcessingConformanceFixture.CreateProcessingArtifact());

        var outcome = await descriptorContext.ExecuteAsync(
            adapter,
            request,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        Assert.HasCount(1, context.GetCurrentInputEvidence());
    }

    [TestMethod]
    public async Task Adapter_MissingRunnerRejectsReplayWithoutInProcessFallback()
    {
        var options = CreateOptions(CreateSocketPath());
        await using var client = new LocalReplayRunnerClient(options);
        var adapter = CreateAdapter(client);
        var context = CreateProcessingContext(ProcessingGraphExecutionClass.Replay);
        context.BeginNode("preview", [], ["$raw"]);
        var request = ProcessingConformanceFixture.CreateRequest(
            ProcessingConformanceFixture.CreateProcessingArtifact());

        await Assert.ThrowsAsync<LocalReplayRunnerUnavailableException>(async () =>
            await adapter.ExecuteAsync(context, request, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ReplayRunnerUnavailable_DefersGraphWithoutConsumingAnAttempt()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode(
                "external",
                new UnavailableDescriptorStep(),
                [],
                true,
                BuiltInProcessingRecipes.NoOpAnalyzer,
                FrameArtifactRole.Metadata,
                "external")
        ]);
        var execution = CreateExecution(ProcessingGraphExecutionClass.Replay);
        var item = new FrameProcessingItem(
            ProcessingConformanceFixture.CameraConfig,
            CreateSubmission(),
            Execution: execution,
            WorkId: execution.WorkId,
            LeaseToken: execution.LeaseToken);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            item,
            graph,
            null,
            telemetry,
            attempt: 1,
            NullLogger.Instance,
            CancellationToken.None,
            durableAttempt: 1).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.Deferred, result.Outcome);
        Assert.AreEqual("processing.replay-runner-unavailable", result.Reason);
    }

    [TestMethod]
    public async Task RunnerCancellation_PropagatesToActiveRecipe()
    {
        var socketPath = CreateSocketPath();
        var options = CreateOptions(socketPath);
        var executor = new BlockingExecutor();
        using var stopping = new CancellationTokenSource();
        await using var server = new LocalReplayRunnerServer(options, executor);
        var serverTask = server.RunAsync(stopping.Token);
        await WaitForSocketAsync(socketPath).ConfigureAwait(false);
        await using var client = new LocalReplayRunnerClient(options);
        using var executionCancellation = new CancellationTokenSource();
        var execution = client.ExecuteAsync(
            CreateJobContext(),
            ProcessingConformanceFixture.CreateRequest(
                ProcessingConformanceFixture.CreateProcessingArtifact()),
            executionCancellation.Token);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
        Assert.IsFalse(execution.IsCompleted, "Runner heartbeats must keep a long recipe connection alive.");

        await executionCancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await execution.ConfigureAwait(false));
        await executor.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await StopServerAsync(stopping, serverTask).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RunnerCapacity_DefersAdditionalJobWithoutUnboundedQueueing()
    {
        var socketPath = CreateSocketPath();
        var options = CreateOptions(socketPath);
        var executor = new BlockingExecutor();
        using var stopping = new CancellationTokenSource();
        await using var server = new LocalReplayRunnerServer(options, executor);
        var serverTask = server.RunAsync(stopping.Token);
        await WaitForSocketAsync(socketPath).ConfigureAwait(false);
        await using var firstClient = new LocalReplayRunnerClient(options);
        await using var secondClient = new LocalReplayRunnerClient(options);
        using var firstCancellation = new CancellationTokenSource();
        var request = ProcessingConformanceFixture.CreateRequest(
            ProcessingConformanceFixture.CreateProcessingArtifact());
        var first = firstClient.ExecuteAsync(CreateJobContext(), request, firstCancellation.Token);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        var unavailable = await Assert.ThrowsAsync<LocalReplayRunnerUnavailableException>(async () =>
            await secondClient.ExecuteAsync(CreateJobContext(), request).ConfigureAwait(false));

        Assert.AreEqual(TimeSpan.FromMilliseconds(100), unavailable.RetryAfter);
        Assert.AreEqual(ReplayProtocol.Version, (await secondClient.ProbeAsync().ConfigureAwait(false)).ProtocolVersion);
        await firstCancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await first.ConfigureAwait(false));
        await StopServerAsync(stopping, serverTask).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Runner_RejectsDuplicateCompletedJobId()
    {
        var socketPath = CreateSocketPath();
        var options = CreateOptions(socketPath);
        using var stopping = new CancellationTokenSource();
        await using var server = new LocalReplayRunnerServer(options);
        var serverTask = server.RunAsync(stopping.Token);
        await WaitForSocketAsync(socketPath).ConfigureAwait(false);
        await using var client = new LocalReplayRunnerClient(options);
        var request = ProcessingConformanceFixture.CreateRequest(
            ProcessingConformanceFixture.CreateProcessingArtifact());
        var context = CreateJobContext();
        _ = await client.ExecuteAsync(context, request).ConfigureAwait(false);
        await Task.Delay(50).ConfigureAwait(false);

        var duplicate = await Assert.ThrowsAsync<LocalReplayRunnerUnavailableException>(async () =>
            await client.ExecuteAsync(context, request).ConfigureAwait(false));
        Assert.IsInstanceOfType<LocalReplayRunnerProtocolException>(duplicate.InnerException);

        await StopServerAsync(stopping, serverTask).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ClientRejectsUnauthenticatedRunnerBeforeRecipeOrPayloadDispatch()
    {
        var socketPath = CreateSocketPath();
        var serverOptions = CreateOptions(
            socketPath,
            Encoding.UTF8.GetBytes("different-local-runner-key-00001"));
        var executor = new CountingExecutor();
        using var stopping = new CancellationTokenSource();
        await using var server = new LocalReplayRunnerServer(serverOptions, executor);
        var serverTask = server.RunAsync(stopping.Token);
        await WaitForSocketAsync(socketPath).ConfigureAwait(false);
        await using var client = new LocalReplayRunnerClient(CreateOptions(socketPath));

        await Assert.ThrowsAsync<LocalReplayRunnerUnavailableException>(async () =>
            await client.ExecuteAsync(
                CreateJobContext(),
                ProcessingConformanceFixture.CreateRequest(
                    ProcessingConformanceFixture.CreateProcessingArtifact())).ConfigureAwait(false));

        Assert.AreEqual(0, executor.ExecutionCount);
        await StopServerAsync(stopping, serverTask).ConfigureAwait(false);
    }

    [TestMethod]
    public void AuthorizationBindsCanonicalRequestAndPayloadDeclarations()
    {
        var options = CreateOptions(CreateSocketPath());
        var context = CreateJobContext();
        var projection = ReplayProjection.ProjectRequest(
            AuthenticationKey,
            context,
            ProcessingConformanceFixture.CreateRequest(
                ProcessingConformanceFixture.CreateProcessingArtifact()),
            options,
            DateTimeOffset.UtcNow);
        var tamperedInput = projection.Metadata.Request.Inputs.Single() with
        {
            PayloadSha256 = new string('F', 64)
        };
        var tamperedRequest = projection.Metadata.Request with
        {
            OutputVariant = "tampered",
            Inputs = [tamperedInput]
        };
        var tamperedSha256 = ReplayProtocol.ComputeSha256(
            ReplayProtocol.SerializeMetadata(tamperedRequest, options.MaxMetadataBytes));

        Assert.IsFalse(ReplayAuthorization.Validate(
            AuthenticationKey,
            projection.Metadata.Authorization,
            tamperedSha256,
            DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public async Task Projection_AcceptsLowercaseSemanticHashesButRejectsLowercasePayloadDigest()
    {
        var options = CreateOptions(CreateSocketPath());
        var semanticSha256 = Convert.ToHexStringLower(Convert.FromHexString(
            ProcessingConformanceFixture.CreateProcessingArtifact().RecipeIdentitySha256));
        var auxiliaryPayload = "{}"u8.ToArray();
        var auxiliarySha256 = Convert.ToHexStringLower(Convert.FromHexString(
            ReplayProtocol.ComputeSha256(auxiliaryPayload)));
        var artifact = ProcessingConformanceFixture.CreateProcessingArtifact() with
        {
            RecipeIdentitySha256 = semanticSha256,
            Compatibility = ProcessingConformanceFixture.Compatibility with
            {
                LocationIdentitySha256 = semanticSha256
            },
            ContentIdentitySha256 = semanticSha256,
            DescriptorIdentitySha256 = semanticSha256
        };
        var request = ProcessingConformanceFixture.CreateRequest(artifact) with
        {
            Input = ProcessingInputSelector.RecipeResult(FrameArtifactRole.Raw, "source", semanticSha256),
            AuxiliaryInputs =
            [
                new ProcessingAuxiliaryInput(
                    "context",
                    ProcessingAuxiliaryInputKind.CanonicalJson,
                    ProcessingInputSelector.RecipeResult(FrameArtifactRole.Metadata, "context", semanticSha256),
                    "context-v1",
                    auxiliarySha256,
                    auxiliaryPayload,
                    Guid.NewGuid())
                {
                    ChecksumSha256 = auxiliarySha256
                }
            ]
        };

        var projection = ReplayProjection.ProjectRequest(
            AuthenticationKey,
            CreateJobContext(),
            request,
            options,
            DateTimeOffset.UtcNow);
        var input = projection.Metadata.Request.Inputs.Single();
        var lowercasePayloadDigest = input with
        {
            PayloadSha256 = Convert.ToHexStringLower(Convert.FromHexString(input.PayloadSha256))
        };
        var invalidEnvelope = projection.Metadata with
        {
            Request = projection.Metadata.Request with { Inputs = [lowercasePayloadDigest] }
        };

        Assert.Throws<LocalReplayRunnerProtocolException>(() => ReplayProjection.ValidateRequestEnvelope(
            invalidEnvelope,
            options,
            DateTimeOffset.UtcNow,
            validateAuthorizationTime: false));

        var executableRequest = ProcessingConformanceFixture.CreateRequest(artifact);
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(executableRequest).ConfigureAwait(false);
        _ = ReplayProjection.ProjectResponse(CreateJobContext(), executableRequest, outcome, options);
    }

    [TestMethod]
    public void DescriptorOnlyFrameLayoutDoesNotRequirePixelPayloadTransfer()
    {
        var options = CreateOptions(CreateSocketPath());
        var input = ProcessingConformanceFixture.CreateProcessingArtifact() with
        {
            Payload = ReadOnlyMemory<byte>.Empty
        };
        var request = ProcessingConformanceFixture.CreateRequest(input);

        var projection = ReplayProjection.ProjectRequest(
            AuthenticationKey,
            CreateJobContext(),
            request,
            options,
            DateTimeOffset.UtcNow);

        Assert.IsEmpty(projection.Payloads);
        Assert.AreEqual(0, projection.Metadata.Request.Inputs.Single().PayloadLength);
        Assert.IsNotNull(projection.Metadata.Request.Inputs.Single().Layout);
    }

    [TestMethod]
    public void ProcessingOptions_InProcessProfileDoesNotRequireRunnerCredentials()
    {
        var options = new ProcessingGraphExecutionOptions();

        var results = Validate(options);

        Assert.IsEmpty(results);
    }

    [TestMethod]
    public void ProcessingOptions_LocalRunnerProfileRequiresExactlyOneCredentialSource()
    {
        var missing = new ProcessingGraphExecutionOptions
        {
            ReplayProfile = ReplayExecutionProfile.LocalRunner
        };
        var duplicate = new ProcessingGraphExecutionOptions
        {
            ReplayProfile = ReplayExecutionProfile.LocalRunner,
            LocalRunner = new LocalReplayRunnerHostOptions
            {
                AuthorizationKey = Encoding.UTF8.GetString(AuthenticationKey),
                AuthorizationKeyFile = "/run/hvo-secrets/replay-runner-auth-key"
            }
        };

        Assert.IsNotEmpty(Validate(missing));
        Assert.IsNotEmpty(Validate(duplicate));
    }

    [TestMethod]
    public void ProcessingOptions_LocalRunnerProfileRejectsAggregateTransferReservation()
    {
        var runner = new LocalReplayRunnerHostOptions
        {
            AuthorizationKey = Encoding.UTF8.GetString(AuthenticationKey)
        };
        var valid = new ProcessingGraphExecutionOptions
        {
            ReplayProfile = ReplayExecutionProfile.LocalRunner,
            ReplayMaximumConcurrency = 2,
            LocalRunner = runner
        };
        var invalid = new ProcessingGraphExecutionOptions
        {
            ReplayProfile = ReplayExecutionProfile.LocalRunner,
            ReplayMaximumConcurrency = 3,
            LocalRunner = runner
        };

        Assert.IsEmpty(Validate(valid));
        Assert.IsTrue(Validate(invalid).Any(result =>
            result.MemberNames.Contains(nameof(ProcessingGraphExecutionOptions.ReplayMaximumConcurrency), StringComparer.Ordinal)));
    }

    [TestMethod]
    public async Task RunnerCapabilities_MissingRecipeDefersBeforePayloadTransfer()
    {
        var socketPath = CreateSocketPath();
        var options = CreateOptions(socketPath);
        var capabilities = ReplayRunnerCapabilities.Create(options);
        capabilities = capabilities with
        {
            BuiltInRecipes = capabilities.BuiltInRecipes.Where(recipe => !string.Equals(
                recipe.Name,
                BuiltInProcessingRecipes.EncodedPreview,
                StringComparison.Ordinal)).ToArray()
        };
        using var stopping = new CancellationTokenSource();
        await using var server = new LocalReplayRunnerServer(options, capabilities: capabilities);
        var serverTask = server.RunAsync(stopping.Token);
        await WaitForSocketAsync(socketPath).ConfigureAwait(false);
        await using var client = new LocalReplayRunnerClient(options);

        await Assert.ThrowsAsync<LocalReplayRunnerUnavailableException>(async () =>
            await client.ExecuteAsync(
                CreateJobContext(),
                ProcessingConformanceFixture.CreateRequest(
                    ProcessingConformanceFixture.CreateProcessingArtifact())).ConfigureAwait(false));

        await StopServerAsync(stopping, serverTask).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RunnerCrash_ClosesClaimAndReturnsUnavailable()
    {
        var socketPath = CreateSocketPath();
        var options = CreateOptions(socketPath);
        var executor = new BlockingExecutor();
        using var stopping = new CancellationTokenSource();
        await using var server = new LocalReplayRunnerServer(options, executor);
        var serverTask = server.RunAsync(stopping.Token);
        await WaitForSocketAsync(socketPath).ConfigureAwait(false);
        await using var client = new LocalReplayRunnerClient(options);
        var execution = client.ExecuteAsync(
            CreateJobContext(),
            ProcessingConformanceFixture.CreateRequest(
                ProcessingConformanceFixture.CreateProcessingArtifact()));
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        await stopping.CancelAsync().ConfigureAwait(false);
        await serverTask.ConfigureAwait(false);

        await Assert.ThrowsAsync<LocalReplayRunnerUnavailableException>(async () =>
            await execution.ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ExecutorFault_AfterHeartbeatReturnsOneValidFailureFrame()
    {
        var socketPath = CreateSocketPath();
        var options = CreateOptions(socketPath);
        using var stopping = new CancellationTokenSource();
        await using var server = new LocalReplayRunnerServer(options, new DelayedFaultingExecutor());
        var serverTask = server.RunAsync(stopping.Token);
        await WaitForSocketAsync(socketPath).ConfigureAwait(false);
        await using var client = new LocalReplayRunnerClient(options);

        var failure = await Assert.ThrowsAsync<LocalReplayRunnerProtocolException>(async () =>
            await client.ExecuteAsync(
                CreateJobContext(),
                ProcessingConformanceFixture.CreateRequest(
                    ProcessingConformanceFixture.CreateProcessingArtifact())).ConfigureAwait(false));

        StringAssert.Contains(failure.Message, "runner-failure", StringComparison.Ordinal);
        Assert.AreEqual(ReplayProtocol.Version, (await client.ProbeAsync().ConfigureAwait(false)).ProtocolVersion);
        await StopServerAsync(stopping, serverTask).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OutputValidation_RejectsTamperedRunnerPayload()
    {
        var options = CreateOptions(CreateSocketPath());
        var request = ProcessingConformanceFixture.CreateRequest(
            ProcessingConformanceFixture.CreateProcessingArtifact());
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);
        var context = CreateJobContext();
        var projection = ReplayProjection.ProjectResponse(context, request, outcome, options);
        var tampered = projection.Payloads.Select(static payload => payload.ToArray()).ToArray();
        tampered[0][0] ^= 0xff;

        Assert.Throws<LocalReplayRunnerOutputValidationException>(() =>
            ReplayProjection.ReconstructOutcome(
                projection.Metadata,
                tampered,
                context,
                request,
                options.MaxTotalTransferBytes));

        var wrongRole = projection.Metadata with
        {
            Products = [projection.Metadata.Products.Single() with { Role = FrameArtifactRole.Raw }]
        };
        Assert.Throws<LocalReplayRunnerOutputValidationException>(() =>
            ReplayProjection.ReconstructOutcome(
                wrongRole,
                projection.Payloads.Select(static payload => payload.ToArray()).ToArray(),
                context,
                request,
                options.MaxTotalTransferBytes));

        var product = projection.Metadata.Products.Single();
        foreach (var invalidProduct in new[]
        {
            product with { MediaType = "image/jpeg" },
            product with { Kind = ProcessingProductKind.Metadata },
            product with { Algorithms = [] },
            product with { TotalIntegration = product.TotalIntegration.Add(TimeSpan.FromTicks(1)) },
            product with { Compatibility = product.Compatibility with { Rig = "tampered-rig" } }
        })
        {
            var invalid = projection.Metadata with { Products = [invalidProduct] };
            Assert.Throws<LocalReplayRunnerOutputValidationException>(() =>
                ReplayProjection.ReconstructOutcome(
                    invalid,
                    projection.Payloads.Select(static payload => payload.ToArray()).ToArray(),
                    context,
                    request,
                    options.MaxTotalTransferBytes));
        }

        var jpegRequest = request with
        {
            Options = JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Jpeg"))
        };
        var jpegOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(jpegRequest).ConfigureAwait(false);
        var jpegProjection = ReplayProjection.ProjectResponse(context, jpegRequest, jpegOutcome, options);
        using var jpegCancellation = new CancellationTokenSource();
        await jpegCancellation.CancelAsync().ConfigureAwait(false);
        Assert.Throws<OperationCanceledException>(() => ReplayProjection.ReconstructOutcome(
            jpegProjection.Metadata,
            jpegProjection.Payloads.Select(static payload => payload.ToArray()).ToArray(),
            context,
            jpegRequest,
            options.MaxTotalTransferBytes,
            jpegCancellation.Token));
        var validJpegPayload = jpegProjection.Payloads.Single().ToArray();
        var invalidJpegPayload = validJpegPayload[..^2];
        var inspectedJpeg = JpegImageCodec.InspectJpeg(invalidJpegPayload);
        Assert.AreEqual(ProcessingConformanceFixture.Layout.Width, inspectedJpeg.Width);
        Assert.Throws<InvalidOperationException>(() => JpegImageCodec.DecodeJpeg(invalidJpegPayload));
        var invalidJpegSha256 = ReplayProtocol.ComputeSha256(invalidJpegPayload);
        var invalidJpeg = jpegProjection.Metadata with
        {
            Products =
            [
                jpegProjection.Metadata.Products.Single() with
                {
                    PayloadLength = invalidJpegPayload.Length,
                    PayloadSha256 = invalidJpegSha256,
                    ChecksumSha256 = invalidJpegSha256
                }
            ]
        };
        Assert.Throws<LocalReplayRunnerOutputValidationException>(() =>
            ReplayProjection.ReconstructOutcome(
                invalidJpeg,
                [invalidJpegPayload],
                context,
                jpegRequest,
                options.MaxTotalTransferBytes));

        var noOpRequest = request with
        {
            RecipeName = BuiltInProcessingRecipes.NoOpAnalyzer,
            Options = JsonSerializer.SerializeToElement(new { })
        };
        var noOpOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(noOpRequest).ConfigureAwait(false);
        var noOpProjection = ReplayProjection.ProjectResponse(context, noOpRequest, noOpOutcome, options);
        var invalidJsonPayload = "{\"status\":\"tampered\"}"u8.ToArray();
        var invalidJsonSha256 = ReplayProtocol.ComputeSha256(invalidJsonPayload);
        var invalidJson = noOpProjection.Metadata with
        {
            Products =
            [
                noOpProjection.Metadata.Products.Single() with
                {
                    PayloadLength = invalidJsonPayload.Length,
                    PayloadSha256 = invalidJsonSha256,
                    ChecksumSha256 = invalidJsonSha256
                }
            ]
        };
        Assert.Throws<LocalReplayRunnerOutputValidationException>(() =>
            ReplayProjection.ReconstructOutcome(
                invalidJson,
                [invalidJsonPayload],
                context,
                noOpRequest,
                options.MaxTotalTransferBytes));

        var imageQualityRequest = request with
        {
            RecipeName = BuiltInProcessingRecipes.ImageQuality,
            Options = JsonSerializer.SerializeToElement(new { })
        };
        var imageQualityOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(imageQualityRequest)
            .ConfigureAwait(false);
        var imageQualityProjection = ReplayProjection.ProjectResponse(
            context, imageQualityRequest, imageQualityOutcome, options);
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var statistics = JsonSerializer.Deserialize<ImageStatisticsResult>(
            imageQualityOutcome.Products.Single().Payload.Span,
            serializerOptions)!;
        var invalidStatisticsPayload = Encoding.UTF8.GetBytes(CaptureContractJson.Canonicalize(
            JsonSerializer.SerializeToElement(statistics with { SampleCount = statistics.SampleCount + 1 }, serializerOptions))
            .GetRawText());
        var invalidStatisticsSha256 = ReplayProtocol.ComputeSha256(invalidStatisticsPayload);
        var invalidStatistics = imageQualityProjection.Metadata with
        {
            Products =
            [
                imageQualityProjection.Metadata.Products.Single() with
                {
                    PayloadLength = invalidStatisticsPayload.Length,
                    PayloadSha256 = invalidStatisticsSha256,
                    ChecksumSha256 = invalidStatisticsSha256
                }
            ]
        };
        Assert.Throws<LocalReplayRunnerOutputValidationException>(() =>
            ReplayProjection.ReconstructOutcome(
                invalidStatistics,
                [invalidStatisticsPayload],
                context,
                imageQualityRequest,
                options.MaxTotalTransferBytes));
    }

    [TestMethod]
    public async Task OutputValidation_AcceptsSelectedRollingWindowAndRejectsReorderedLineage()
    {
        var options = CreateOptions(CreateSocketPath());
        var source = ProcessingConformanceFixture.CreateProcessingArtifact();
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var inputs = Enumerable.Range(0, 3)
            .Select(index => source with
            {
                ArtifactId = Guid.NewGuid(),
                CreatedUtc = start.AddSeconds(index),
                CaptureSequence = index + 1
            })
            .ToArray();
        var request = ProcessingConformanceFixture.CreateRequest(source) with
        {
            RecipeName = BuiltInProcessingRecipes.RollingMean,
            Options = JsonSerializer.SerializeToElement(new RollingMeanOptions(2)),
            Inputs = inputs,
            OutputVariant = "rolling-two",
            InputArtifactId = null
        };
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            inputs[1..].Select(static input => input.ArtifactId).ToArray(),
            outcome.Products.Single().SourceArtifactIds.ToArray());
        var context = CreateJobContext();
        var projection = ReplayProjection.ProjectResponse(context, request, outcome, options);

        var reconstructed = ReplayProjection.ReconstructOutcome(
            projection.Metadata,
            projection.Payloads.Select(static payload => payload.ToArray()).ToArray(),
            context,
            request,
            options.MaxTotalTransferBytes);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, reconstructed.Status);
        var product = projection.Metadata.Products.Single();
        var reversedSources = product.SourceArtifactIds.Reverse().ToArray();
        var reordered = projection.Metadata with
        {
            Products =
            [
                product with
                {
                    SourceArtifactIds = reversedSources,
                    OutputIdentitySha256 = ProcessingIdentity.CreateOutputIdentity(
                        product.Role,
                        product.Variant,
                        product.Recipe.IdentitySha256,
                        reversedSources)
                }
            ]
        };
        Assert.Throws<LocalReplayRunnerOutputValidationException>(() =>
            ReplayProjection.ReconstructOutcome(
                reordered,
                projection.Payloads.Select(static payload => payload.ToArray()).ToArray(),
                context,
                request,
                options.MaxTotalTransferBytes));
    }

    private static CameraAgentRecipeExecutionAdapter CreateAdapter(LocalReplayRunnerClient client)
    {
        var hostOptions = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.GetTempPath(),
            ProcessingGraphs = new ProcessingGraphExecutionOptions
            {
                ReplayProfile = ReplayExecutionProfile.LocalRunner,
                LocalRunner = new LocalReplayRunnerHostOptions
                {
                    AuthorizationKey = Encoding.UTF8.GetString(AuthenticationKey)
                }
            }
        });
        return new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor(), hostOptions, client);
    }

    private static CaptureProcessingContext CreateProcessingContext(ProcessingGraphExecutionClass executionClass)
        => new(
            ProcessingConformanceFixture.CameraConfig,
            CreateSubmission(),
            null,
            null,
            CreateExecution(executionClass));

    private static ProcessingExecutionContext CreateExecution(ProcessingGraphExecutionClass executionClass) => new(
        Guid.NewGuid(),
        executionClass,
        "basic@1",
        new string('A', 64),
        AllowAutomaticPublication: executionClass == ProcessingGraphExecutionClass.Live,
        WorkId: 1,
        LeaseToken: "lease-token",
        LeaseOwner: "test-owner",
        DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(5),
        DurableAttempt: 1);

    private static CaptureLoopSubmission CreateSubmission()
    {
        var now = DateTimeOffset.UtcNow;
        return new CaptureLoopSubmission(
            new CaptureRequest(now, TimeSpan.FromSeconds(1), CaptureMode.Still),
            new CaptureResult(
                null,
                new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null),
                TimeSpan.Zero,
                CaptureMode.Still,
                false),
            now,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero);
    }

    private static ReplayRunnerJobContext CreateJobContext() => ReplayRunnerJobContext.Create(
        Guid.NewGuid().ToString("N"),
        "basic@1",
        new string('A', 64),
        "preview",
        1,
        "lease-token",
        DateTimeOffset.UtcNow.AddMinutes(5));

    private static LocalReplayRunnerOptions CreateOptions(
        string socketPath,
        byte[]? authenticationKey = null,
        long maximumTransferBytes = 64 * 1024 * 1024,
        int maximumConcurrency = 1) => new()
        {
            SocketPath = socketPath,
            PreSharedAuthKey = authenticationKey ?? AuthenticationKey,
            MaxConcurrency = maximumConcurrency,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            HeartbeatTimeout = TimeSpan.FromSeconds(2),
            MaxMetadataBytes = 1024 * 1024,
            MaxTotalTransferBytes = maximumTransferBytes
        };

    private sealed class CountingExecutor : IProcessingRecipeExecutor
    {
        public int ExecutionCount { get; private set; }

        public ValueTask<ProcessingOutcome> ExecuteAsync(
            ProcessingExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure("unexpected-execution"));
        }
    }

    private sealed class DelayedFaultingExecutor : IProcessingRecipeExecutor
    {
        public async ValueTask<ProcessingOutcome> ExecuteAsync(
            ProcessingExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Synthetic executor failure.");
        }
    }

    private sealed class BlockingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
    }

    private static string CreateSocketPath() => Path.Combine(
        Path.GetTempPath(),
        $"hvo-replay-{Guid.NewGuid():N}.sock");

    private static async Task WaitForSocketAsync(string socketPath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!Path.Exists(socketPath))
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }
    }

    private static async Task StopServerAsync(CancellationTokenSource stopping, Task serverTask)
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        await serverTask.ConfigureAwait(false);
    }

    private static List<ValidationResult> Validate(object value)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true);
        return results;
    }

    private sealed class UnavailableDescriptorStep : IDescriptorOnlyCaptureProcessingStep
    {
        public string Name => "external";

        public int Order => 0;

        public ValueTask ProcessAsync(
            CaptureDescriptorProcessingContext context,
            CancellationToken cancellationToken)
            => throw new LocalReplayRunnerUnavailableException("runner missing");
    }

    private sealed class BlockingExecutor : IProcessingRecipeExecutor
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ProcessingOutcome> ExecuteAsync(
            ProcessingExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                cancellationToken.WaitHandle.WaitOne();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Canceled.TrySetResult();
                throw;
            }
            throw new InvalidOperationException("The blocking test executor completed without cancellation.");
        }
    }
}
