using System.Globalization;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCloudPerformanceTests
{
    private const int AssessmentMeasurementCount = 30;
    private const int AssessmentCaptureCheckpoint = 10;
    private static readonly string[] AssessmentOperations =
    [
        "estimator-mask-on",
        "estimator-mask-off",
        "assessment-host-mask-on",
        "assessment-host-mask-off",
        "renderer",
        "overlay-host"
    ];
    private static readonly Dictionary<(string Workload, string Scenario, string Operation), string>
        AssessmentExpectedChecksums = new()
        {
            [("W1", "clear", "estimator-mask-on")] = "4AD37853FD5453B41C55016447C74C67F77F1BE48224125112362BD06326BAB8",
            [("W1", "clear", "estimator-mask-off")] = "23E6C70F8011E06999CF974A419FB9578D7B9A5FED5DA22B9A938652FED53DD7",
            [("W1", "clear", "assessment-host-mask-on")] = "574A85CC9B08E9046A954E3F8A65BE01BF7BE2327A722B3E89E5E33A77CD42FD",
            [("W1", "clear", "assessment-host-mask-off")] = "EC96470BDB3EE7F810A0F2C92C600D1BFB9939F60F702BFDAA72116D97F4D824",
            [("W1", "clear", "renderer")] = "661B5381D0B6B63F7F2C5FF68500B8A7D7AAF4FDFF192F2D148D363A9D4B1CAB",
            [("W1", "clear", "overlay-host")] = "661B5381D0B6B63F7F2C5FF68500B8A7D7AAF4FDFF192F2D148D363A9D4B1CAB",
            [("W1", "partial", "estimator-mask-on")] = "A57DE6A499F24BBB21A3501C515A79AAD91732BB4E7505139A633591C3A4C947",
            [("W1", "partial", "estimator-mask-off")] = "FAA1106CFE00AFE5CA27BD5B395C99D64C5F7EAE4F7EEAEDD69BA71F2A71A44B",
            [("W1", "partial", "assessment-host-mask-on")] = "8CB7887B738E350B5FAD36A4AFAEDA1FB6890935495A8CB07613D2B31CE68736",
            [("W1", "partial", "assessment-host-mask-off")] = "8DC098FFD134403B722ECAC86F8966C8C38583D8D8C1E0C034A5476D0D152CE0",
            [("W1", "partial", "renderer")] = "3A103A39C831B77B7AF7C080935A31F048480730CE275CC8A49C2069D267A046",
            [("W1", "partial", "overlay-host")] = "3A103A39C831B77B7AF7C080935A31F048480730CE275CC8A49C2069D267A046",
            [("W1", "overcast", "estimator-mask-on")] = "1C73D90664755967AB87CCA9D5CFEB91B52EEE4D2608B1264AA31BA5331F78B8",
            [("W1", "overcast", "estimator-mask-off")] = "D00353ACB04A8F1F9F36D8431F01241341937E038AE4124FDDA89BA9AD0D0ACF",
            [("W1", "overcast", "assessment-host-mask-on")] = "8F0097085DD0BCEB4685990273DFC44D1A1A8554B707DE035F9ED6CBDDDD5267",
            [("W1", "overcast", "assessment-host-mask-off")] = "114EF7C4C95E784053A710D535DF4B308A32A8CFA15833A00DC1957D431BE205",
            [("W1", "overcast", "renderer")] = "FB9C285AD309D419B8502A8348A26AE291F6D1F5B461108AAA734248D70F195B",
            [("W1", "overcast", "overlay-host")] = "FB9C285AD309D419B8502A8348A26AE291F6D1F5B461108AAA734248D70F195B",
            [("W2", "clear", "estimator-mask-on")] = "B0D131E2EA5A27C806EDDA05D978342D19D98295516EB28D1C43C1EE13BEB721",
            [("W2", "clear", "estimator-mask-off")] = "0DC5DE9358D074C6B7A888EC7607D7BEC0B16C18E402D9D7E7D146A3E523038D",
            [("W2", "clear", "assessment-host-mask-on")] = "7C100AF7DE875CAE34521E23F7FA98427F3CACE34DB164DCD1EBB82C8D07E7D7",
            [("W2", "clear", "assessment-host-mask-off")] = "52AE58B7990A332DD3BBD71C59ED11FB3BEABF9FDA7825F74302C454F37255E3",
            [("W2", "clear", "renderer")] = "C14DDDF5AF17ABCFAE4ADCE2F0CB4BD4E4F2F0BD94CA06A3CE23BF200E5FCE62",
            [("W2", "clear", "overlay-host")] = "C14DDDF5AF17ABCFAE4ADCE2F0CB4BD4E4F2F0BD94CA06A3CE23BF200E5FCE62",
            [("W2", "partial", "estimator-mask-on")] = "6C35CA56EDC5F2D8BF30148CD8256BD2917DD07B2C254695C270C87F88693011",
            [("W2", "partial", "estimator-mask-off")] = "FF76371B27B472361BD742F7B580A9924F38DFDBEDB1E80EB60CBCE1D6C2848B",
            [("W2", "partial", "assessment-host-mask-on")] = "7FB04D7A5F66F4056A8B15F8BAA107DC5C0C93C7759A22F843717FF7B1BAC3D3",
            [("W2", "partial", "assessment-host-mask-off")] = "8899C0F126C5DFBF72F6A4BA80577DB93C596DB2890C39E46F4584FC6C2B8C24",
            [("W2", "partial", "renderer")] = "6D2CCE8E4C95C700E9DD33869B089F8D1681A9831A0322847169FF250B86EBB5",
            [("W2", "partial", "overlay-host")] = "6D2CCE8E4C95C700E9DD33869B089F8D1681A9831A0322847169FF250B86EBB5",
            [("W2", "overcast", "estimator-mask-on")] = "10EF8B565AEFF20820B4FA627FD005EDBE547343E6A5DC40A6093B9EFB366548",
            [("W2", "overcast", "estimator-mask-off")] = "1477C7FC2741E97C09CB42AA2B1D1CF02DE698D5ACE5692BD3D1EF941BB400C2",
            [("W2", "overcast", "assessment-host-mask-on")] = "1308B7417F0D70AC8DAB2C8FA040DF4399A58B8430D2806E73D0906CF1DC3371",
            [("W2", "overcast", "assessment-host-mask-off")] = "7817C44FD683EF68F8B500D786260A4BA346A6828C5FAA4FAE4D3CAC09B859EF",
            [("W2", "overcast", "renderer")] = "29B3DEB44F389B3840E2612EA4514502595FD2AB1FA324F4E4EDCAFDF611F4C1",
            [("W2", "overcast", "overlay-host")] = "29B3DEB44F389B3840E2612EA4514502595FD2AB1FA324F4E4EDCAFDF611F4C1"
        };
    private static readonly Dictionary<(string Workload, string Scenario), (string Current, string Reference)>
        AssessmentExpectedInputs = new()
        {
            [("W1", "clear")] = ("A8C6C64048404D7293903E1FE135E549DE5D3D5C84C80BBF91A511CE95B324F8", "A8C6C64048404D7293903E1FE135E549DE5D3D5C84C80BBF91A511CE95B324F8"),
            [("W1", "partial")] = ("867359F13A3F310C0077B1C3DF46C38E2EC3D130895EB867C835F0E66B9EEF10", "4830A39471FBCD969FC8D53E2867C2081DA9E7125F20E541C3DE19509C728E92"),
            [("W1", "overcast")] = ("7D78CB79B3F63748E44DF98F815D984A2F1E4C96A1B471C4CD3C3A5ABB6B99E9", "16EB066481F4679EC1487F51FD9681D3529F5E6B7B7D5EBE3B95888F814EDEE0"),
            [("W2", "clear")] = ("19E07429686246587F7EBF43F8620BFB9DBCD6E72964906180137C71029BD205", "19E07429686246587F7EBF43F8620BFB9DBCD6E72964906180137C71029BD205"),
            [("W2", "partial")] = ("BF0DE274E7EA5906A1007D4049BADDF3B21AD71759F2B5161406C305E8278B54", "B99DDDED9CE5C9A05697CD75391EDCED3C04775E8515928CBA2EB6163084107B"),
            [("W2", "overcast")] = ("6FE551C35ABFA6433917FD271A863E8B71CB071A3A1FBF3DA29F5FB93D33E76A", "20869479F54557B2294C757DBBFE82529D642184D8F8F621D87B34BB5A258B8E")
        };

    [TestMethod]
    public async Task W1W2AssessmentEvidence()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("HVO_ISSUE105_PERF_CHILD"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("The aggregate performance test does not run inside a child measurement process.");
        }
        var repositoryRoot = FindRepositoryRoot();
        var outputRoot = Environment.GetEnvironmentVariable("HVO_PERF_OUTPUT")
            ?? Path.Combine(repositoryRoot, "TestResults", "issue-105", "candidate");
        outputRoot = Path.GetFullPath(outputRoot);
        var caseRoot = Path.Combine(outputRoot, "cases");
        if (Directory.Exists(caseRoot))
        {
            Directory.Delete(caseRoot, recursive: true);
        }
        Directory.CreateDirectory(caseRoot);
        Assert.HasCount(36, AssessmentExpectedChecksums);
        Assert.HasCount(6, AssessmentExpectedInputs);

        var measurements = new List<AssessmentPerformanceMeasurement>();
        foreach (var workload in new[] { "W1", "W2" })
        {
            foreach (var scenario in new[] { "clear", "partial", "overcast" })
            {
                foreach (var operation in AssessmentOperations)
                {
                    var casePath = Path.Combine(caseRoot, $"{workload}-{scenario}-{operation}.json");
                    await RunMeasurementChildAsync(
                        repositoryRoot, workload, scenario, operation, casePath).ConfigureAwait(false);
                    var measurement = JsonSerializer.Deserialize<AssessmentPerformanceMeasurement>(
                        await File.ReadAllTextAsync(casePath).ConfigureAwait(false),
                        EvidenceSerializerOptions);
                    Assert.IsNotNull(measurement);
                    measurements.Add(measurement);
                }
            }
        }
        ValidateMeasurementMatrix(measurements);

        var evidence = new
        {
            schemaVersion = "issue-105-cloud-assessment-performance-v2",
            revision = ReadGit(repositoryRoot, "rev-parse", "HEAD"),
            branch = ReadGit(repositoryRoot, "branch", "--show-current"),
            dirtyState = ReadGit(repositoryRoot, "status", "--short"),
            baselineRevision = "35e063202b0d3f38778a021725e3b33000524d7d67",
            recordedUtc = DateTimeOffset.UtcNow,
            environment = new
            {
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                processor = ReadProcessorName(),
                processorCount = Environment.ProcessorCount,
                memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                framework = RuntimeInformation.FrameworkDescription,
                runtimeVersion = Environment.Version.ToString(),
                configuration = "Release",
                nativeMode = "in-process managed recipes; Skia/JPEG not used",
                serviceTopology = "no SQL, SQLite, MinIO, network, or containers"
            },
            workload = new
            {
                ids = new[] { "W1 ASI174 1936x1216 Mono16", "W2 ASI178 3096x2080 RGGB16" },
                scenarios = new[]
                {
                    "clear: #104 explicit-clear scn-104-3a2d5601",
                    "partial: #104 partial-sustained scn-104-6e2074ad",
                    "overcast: #104 overcast scn-104-d5b88a0e"
                },
                virtualSkySeed = 2025,
                cloudSeed = 104,
                captureCheckpoint = AssessmentCaptureCheckpoint,
                hiddenScenarioProvenancePassedToAssessment = false
            },
            method = new
            {
                command = "HVO_PERF_OUTPUT=TestResults/issue-105/candidate dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --configuration Release --filter FullyQualifiedName~VirtualSkyCloudPerformanceTests.W1W2AssessmentEvidence",
                isolation = "one child dotnet test process per workload/scenario/stage",
                warmups = WarmupCount,
                repetitions = AssessmentMeasurementCount,
                percentileMethod = "nearest-rank p95 from 30 sorted independent operations after warmup",
                cpuBoundary = "runner-inclusive testhost process CPU delta around only the measured call",
                allocationBoundary = "per-operation current-thread allocation delta; each measured ValueTask is required to complete synchronously",
                memoryBoundary = "isolated-process working set, OS peak working set, managed live bytes, and post-GC LOH size before/after the measurement helper frame unwinds"
            },
            baseline = new
            {
                W1 = new
                {
                    imageQualityMedianMilliseconds = 2.7538,
                    imageQualityP95Milliseconds = 2.8056,
                    imageQualityAllocatedBytes = 32376,
                    noOpMedianMilliseconds = 0.0761,
                    noOpAllocatedBytes = 28144
                },
                W2 = new
                {
                    imageQualityMedianMilliseconds = 7.3496,
                    imageQualityP95Milliseconds = 8.5196,
                    imageQualityAllocatedBytes = 32376,
                    noOpMedianMilliseconds = 0.0792,
                    noOpAllocatedBytes = 28144
                }
            },
            io = "N/A: benchmark consumes borrowed in-memory frames; every filesystem/SQL/SQLite/MinIO/network operation count is zero.",
            backlog = "N/A for pure/host-adapter benchmark. CentralCloudProcessingIntegrationTests verifies one-job frozen-input lease/retry convergence.",
            result = CreatePerformanceInterpretation(measurements),
            measurements
        };
        Directory.CreateDirectory(outputRoot);
        var outputPath = Path.Combine(outputRoot, "cloud-assessment-performance.json");
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(evidence, EvidenceSerializerOptions)).ConfigureAwait(false);
        TestContext.WriteLine($"Issue #105 performance evidence: {outputPath}");
    }

    [TestMethod]
    public async Task MeasureSelectedAssessmentEvidence()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HVO_ISSUE105_PERF_CHILD"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("This test is invoked only by W1W2AssessmentEvidence child processes.");
        }
        var workloadId = GetRequiredEnvironment("HVO_ISSUE105_PERF_WORKLOAD");
        var scenarioId = GetRequiredEnvironment("HVO_ISSUE105_PERF_SCENARIO");
        var operation = GetRequiredEnvironment("HVO_ISSUE105_PERF_OPERATION");
        var outputPath = GetRequiredEnvironment("HVO_ISSUE105_PERF_CASE_OUTPUT");
        var workload = workloadId switch
        {
            "W1" => Workload.W1,
            "W2" => Workload.W2,
            _ => throw new InvalidOperationException($"Unsupported workload '{workloadId}'.")
        };
        var scenario = scenarioId switch
        {
            "clear" => PerformanceScenario.All.Single(item => item.Id == "explicit-clear"),
            "partial" => PerformanceScenario.All.Single(item => item.Id == "partial-sustained"),
            "overcast" => PerformanceScenario.All.Single(item => item.Id == "overcast"),
            _ => throw new InvalidOperationException($"Unsupported scenario '{scenarioId}'.")
        };
        if (!AssessmentOperations.Contains(operation, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported operation '{operation}'.");
        }

        var inputs = await CreateAssessmentInputsAsync(workload, scenario, scenarioId).ConfigureAwait(false);
        var measured = await CreateMeasuredOperationAsync(inputs, operation).ConfigureAwait(false);
        var result = await MeasureOperationAsync(inputs, scenarioId, measured).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(result, EvidenceSerializerOptions)).ConfigureAwait(false);
    }

    private static async Task RunMeasurementChildAsync(
        string repositoryRoot,
        string workload,
        string scenario,
        string operation,
        string outputPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "test",
                     "tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj",
                     "--no-build",
                     "--configuration",
                     "Release",
                     "--filter",
                     "FullyQualifiedName~VirtualSkyCloudPerformanceTests.MeasureSelectedAssessmentEvidence"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["HVO_ISSUE105_PERF_CHILD"] = "1";
        startInfo.Environment["HVO_ISSUE105_PERF_WORKLOAD"] = workload;
        startInfo.Environment["HVO_ISSUE105_PERF_SCENARIO"] = scenario;
        startInfo.Environment["HVO_ISSUE105_PERF_OPERATION"] = operation;
        startInfo.Environment["HVO_ISSUE105_PERF_CASE_OUTPUT"] = outputPath;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the isolated performance test process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Performance child failed for {workload}/{scenario}/{operation}.\n" +
                await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false));
        }
    }

    private static async Task<AssessmentInputs> CreateAssessmentInputsAsync(
        Workload workload,
        PerformanceScenario scenario,
        string scenarioId)
    {
        var clearDefinition = PerformanceScenario.All.Single(item => item.Id == "explicit-clear").Definition;
        var referenceScenario = new PerformanceScenario(
            $"reference-{scenarioId}", scenario.Exposure, AssessmentMeasurementCount, clearDefinition);
        var referenceCapture = await CaptureScenarioAsync(workload, referenceScenario).ConfigureAwait(false);
        var currentCapture = await CaptureScenarioAsync(workload, scenario).ConfigureAwait(false);
        var current = CreateRawArtifact(
            workload, scenarioId, "current", currentCapture.Frame, currentCapture.Config);
        var reference = CreateRawArtifact(
            workload, scenarioId, "reference", referenceCapture.Frame, referenceCapture.Config);
        if (workload.PixelFormat == CameraPixelFormat.BayerRggb16)
        {
            Assert.AreEqual("14", currentCapture.Frame.Metadata.Extra!["sensorAdcBitDepth"]);
            Assert.AreEqual(64d, current.Layout!.BlackLevel);
            Assert.AreEqual(ushort.MaxValue, current.Layout.WhiteLevel);
            Assert.AreEqual(64d, reference.Layout!.BlackLevel);
            Assert.AreEqual(ushort.MaxValue, reference.Layout.WhiteLevel);
        }
        var environment = new CloudAssessmentEnvironmentV1(
            CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            CaptureSolarRegime.Night,
            EnvironmentalObservationMatchStatus.Missing,
            null,
            null,
            false);
        var environmentPayload = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(environment)));
        var environmentInput = new ProcessingAuxiliaryInput(
            "environment",
            ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            IdentitySha256: ProcessingIdentity.ComputePayloadSha256(environmentPayload),
            Payload: environmentPayload);
        var currentChecksum = ProcessingIdentity.ComputePayloadSha256(current.Payload);
        var referenceChecksum = ProcessingIdentity.ComputePayloadSha256(reference.Payload);
        var expectedInputs = AssessmentExpectedInputs[(workload.Id, scenarioId)];
        Assert.AreEqual(expectedInputs.Current, currentChecksum);
        Assert.AreEqual(expectedInputs.Reference, referenceChecksum);
        return new AssessmentInputs(
            workload.Id,
            workload.Width,
            workload.Height,
            workload.PixelFormat,
            scenario.Id,
            current,
            reference,
            environmentInput,
            currentChecksum,
            referenceChecksum);
    }

    private static async Task<ScenarioCapture> CaptureScenarioAsync(
        Workload workload,
        PerformanceScenario scenario)
    {
        var config = CreateConfig(workload, scenario);
        var module = new VirtualSkyCameraModule(
            TimeProvider.System,
            new InMemoryCelestialCatalog([]),
            new ProjectedSceneStore());
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var result = await CaptureAsync(
            module,
            new CaptureSetpoint(scenario.Exposure, 0, null, null),
            AssessmentCaptureCheckpoint).ConfigureAwait(false);
        Assert.IsNotNull(result.Frame);
        return new ScenarioCapture(config, result.Frame);
    }

    private static ProcessingArtifact CreateRawArtifact(
        Workload workload,
        string scenario,
        string variant,
        CameraFrame frame,
        CameraModuleConfig config)
    {
        var artifactId = CreateGuid($"{workload.Id}-{scenario}-{variant}");
        return CameraAgentRecipeExecutionAdapter.CreateArtifact(
            config,
            new FrameArtifact(
                artifactId,
                FrameArtifactRole.Raw,
                frame,
                recipeVersion: $"{workload.Id}-virtual-sky-raw-v1"),
            variant) with
        {
            CaptureSequence = AssessmentCaptureCheckpoint
        };
    }

    private static async Task<MeasuredAssessmentOperation> CreateMeasuredOperationAsync(
        AssessmentInputs inputs,
        string operation)
    {
        var includeMask = !operation.EndsWith("mask-off", StringComparison.Ordinal);
        var estimatorOptions = new CloudTransmissionEstimatorOptions(
            minimumReferenceSignal: 1,
            includeMask: includeMask);
        var currentFrame = CreateLinearFrame(inputs.Current);
        var referenceFrame = CreateLinearFrame(inputs.Reference);
        if (operation.StartsWith("estimator-", StringComparison.Ordinal))
        {
            return new MeasuredAssessmentOperation(
                operation,
                SourceBytes: inputs.Current.Payload.Length + inputs.Reference.Payload.Length,
                MaximumLiveFullFrameBuffers: 2,
                DeclaredFullFrameCopies: 0,
                Complexity: "O(width*height+tile-count)",
                Execute: () => ValueTask.FromResult<object>(
                    Linear16CloudTransmissionEstimator.Assess(currentFrame, referenceFrame, estimatorOptions)));
        }

        var adapter = new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var assessmentRequest = CreateAssessmentRequest(inputs, includeMask);
        if (operation.StartsWith("assessment-host-", StringComparison.Ordinal))
        {
            return new MeasuredAssessmentOperation(
                operation,
                SourceBytes: inputs.Current.Payload.Length + inputs.Reference.Payload.Length,
                MaximumLiveFullFrameBuffers: 2,
                DeclaredFullFrameCopies: 0,
                Complexity: "O(width*height+tile-count+canonical-json)",
                Execute: async () => await adapter.ExecuteAsync(assessmentRequest, CancellationToken.None)
                    .ConfigureAwait(false));
        }

        var assessment = AssertProduced(await adapter.ExecuteAsync(
            CreateAssessmentRequest(inputs, includeMask: true), CancellationToken.None).ConfigureAwait(false));
        var preview = AssertProduced(await adapter.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.Raw("current"),
            [inputs.Current],
            "cloud-preview",
            InputArtifactId: inputs.Current.ArtifactId), CancellationToken.None).ConfigureAwait(false));
        var previewArtifact = ToProcessingArtifact(preview, inputs.Current.CreatedUtc);
        var assessmentArtifact = ToProcessingArtifact(assessment, inputs.Current.CreatedUtc);
        if (operation == "renderer")
        {
            var parsed = CloudAssessmentJson.Parse(assessment.Payload).Assessment
                ?? throw new InvalidOperationException("Assessment setup output is invalid.");
            var labels = CreateOverlayLabels(parsed);
            var imageLayout = new ImageLayout(
                preview.Layout!.Width,
                preview.Layout.Height,
                preview.Layout.PixelFormat,
                preview.Layout.StrideBytes);
            var mask = parsed.Mask?.Bits.ToArray()
                ?? new byte[(parsed.Grid.Columns * parsed.Grid.Rows + 7) / 8];
            return new MeasuredAssessmentOperation(
                operation,
                SourceBytes: preview.Payload.Length + assessment.Payload.Length,
                MaximumLiveFullFrameBuffers: inputs.PixelFormat == CameraPixelFormat.BayerRggb16 ? 7 : 5,
                DeclaredFullFrameCopies: inputs.PixelFormat == CameraPixelFormat.BayerRggb16 ? 7 : 5,
                Complexity: "O(width*height+tile-count+label-pixels)",
                Execute: () => ValueTask.FromResult<object>(WeatherCloudOverlayRenderer.Render(
                    imageLayout,
                    preview.Payload,
                    parsed.Grid.Columns,
                    parsed.Grid.Rows,
                    mask,
                    labels)));
        }

        var overlayRequest = new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.WeatherCloudOverlay,
            JsonSerializer.SerializeToElement(new WeatherCloudOverlayOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview,
                previewArtifact.Variant,
                previewArtifact.RecipeIdentitySha256),
            [previewArtifact, assessmentArtifact],
            "weather-cloud-overlay-v1",
            AuxiliaryInputs:
            [
                new ProcessingAuxiliaryInput(
                    "assessment",
                    ProcessingAuxiliaryInputKind.Artifact,
                    ProcessingInputSelector.RecipeResult(
                        FrameArtifactRole.Metadata,
                        assessmentArtifact.Variant,
                        assessmentArtifact.RecipeIdentitySha256),
                    ArtifactId: assessmentArtifact.ArtifactId),
                inputs.EnvironmentInput
            ],
            InputArtifactId: previewArtifact.ArtifactId);
        return new MeasuredAssessmentOperation(
            operation,
            SourceBytes: preview.Payload.Length + assessment.Payload.Length,
            MaximumLiveFullFrameBuffers: inputs.PixelFormat == CameraPixelFormat.BayerRggb16 ? 8 : 6,
            DeclaredFullFrameCopies: inputs.PixelFormat == CameraPixelFormat.BayerRggb16 ? 8 : 6,
            Complexity: "O(width*height+tile-count+canonical-json+label-pixels)",
            Execute: async () => await adapter.ExecuteAsync(overlayRequest, CancellationToken.None).ConfigureAwait(false));
    }

    private static async Task<AssessmentPerformanceMeasurement> MeasureOperationAsync(
        AssessmentInputs inputs,
        string scenario,
        MeasuredAssessmentOperation operation)
    {
        for (var warmup = 0; warmup < WarmupCount; warmup++)
        {
            var pending = operation.Execute();
            Assert.IsTrue(pending.IsCompletedSuccessfully,
                "Current-thread allocation evidence requires synchronous recipe completion.");
            _ = ValidateMeasuredOutput(
                await pending.ConfigureAwait(false),
                inputs,
                operation.Name);
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetStart = process.WorkingSet64;
        var peakWorkingSetStart = process.PeakWorkingSet64;
        var managedLiveStart = GC.GetTotalMemory(forceFullCollection: false);
        var lohStart = GC.GetGCMemoryInfo().GenerationInfo[3];
        var generation0Start = GC.CollectionCount(0);
        var generation1Start = GC.CollectionCount(1);
        var generation2Start = GC.CollectionCount(2);
        var loop = RunMeasurementLoop(process, inputs, operation);
        var validated = loop.Validated;
        var expected = AssessmentExpectedChecksums[(inputs.Workload, scenario, operation.Name)];
        Assert.AreEqual(expected, validated.ChecksumSha256);
        Array.Sort(loop.ElapsedMilliseconds);
        process.Refresh();
        var workingSetEndBeforeCollection = process.WorkingSet64;
        var peakWorkingSet = process.PeakWorkingSet64;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        process.Refresh();
        var managedLiveEnd = GC.GetTotalMemory(forceFullCollection: false);
        var lohEnd = GC.GetGCMemoryInfo().GenerationInfo[3];
        var workingSetEnd = process.WorkingSet64;
        var averageMilliseconds = loop.ElapsedMilliseconds.Average();
        return new AssessmentPerformanceMeasurement(
            inputs.Workload,
            scenario,
            inputs.VirtualScenario,
            operation.Name,
            inputs.Width,
            inputs.Height,
            inputs.PixelFormat.ToString(),
            WarmupCount,
            AssessmentMeasurementCount,
            operation.SourceBytes,
            validated.OutputBytes,
            loop.ElapsedMilliseconds[loop.ElapsedMilliseconds.Length / 2],
            loop.ElapsedMilliseconds[(int)Math.Ceiling(loop.ElapsedMilliseconds.Length * 0.95) - 1],
            loop.CpuMilliseconds,
            loop.CpuMilliseconds / AssessmentMeasurementCount,
            loop.AllocatedBytes / (double)AssessmentMeasurementCount,
            workingSetStart,
            workingSetEndBeforeCollection,
            workingSetEnd,
            peakWorkingSet,
            Math.Max(0, peakWorkingSet - peakWorkingSetStart),
            managedLiveStart,
            managedLiveEnd,
            managedLiveEnd - managedLiveStart,
            lohStart.SizeAfterBytes,
            lohEnd.SizeAfterBytes,
            lohEnd.SizeAfterBytes - lohStart.SizeAfterBytes,
            lohStart.FragmentationAfterBytes,
            lohEnd.FragmentationAfterBytes,
            GC.CollectionCount(0) - generation0Start,
            GC.CollectionCount(1) - generation1Start,
            GC.CollectionCount(2) - generation2Start,
            averageMilliseconds == 0 ? 0 : 1000 / averageMilliseconds,
            averageMilliseconds == 0 ? 0 : operation.SourceBytes / 1024d / 1024d / (averageMilliseconds / 1000),
            validated.ChecksumSha256,
            validated.AlgorithmVersion,
            validated.OutputIdentitySha256,
            validated.CoverageMillionths,
            inputs.CurrentChecksumSha256,
            inputs.ReferenceChecksumSha256,
            operation.MaximumLiveFullFrameBuffers,
            operation.DeclaredFullFrameCopies,
            operation.Complexity,
            FilesystemOperations: 0,
            SqliteOperations: 0,
            SqlOperations: 0,
            MinioOperations: 0,
            NetworkOperations: 0,
            DurableBacklogCount: 0,
            DurableBacklogBytes: 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MeasurementLoopResult RunMeasurementLoop(
        Process process,
        AssessmentInputs inputs,
        MeasuredAssessmentOperation operation)
    {
        var elapsed = new double[AssessmentMeasurementCount];
        long allocatedBytes = 0;
        double cpuMilliseconds = 0;
        object? last = null;
        for (var iteration = 0; iteration < AssessmentMeasurementCount; iteration++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var cpuBefore = process.TotalProcessorTime;
            var started = Stopwatch.GetTimestamp();
            var pending = operation.Execute();
            if (!pending.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException(
                    "Current-thread allocation evidence requires synchronous recipe completion.");
            }
            last = pending.Result;
            elapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            cpuMilliseconds += (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            allocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        }
        return new MeasurementLoopResult(
            elapsed,
            cpuMilliseconds,
            allocatedBytes,
            ValidateMeasuredOutput(last!, inputs, operation.Name));
    }

    private static ValidatedAssessmentOutput ValidateMeasuredOutput(
        object output,
        AssessmentInputs inputs,
        string operation)
    {
        if (output is CloudTransmissionResult transmission)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(transmission)));
            Assert.AreEqual(inputs.Current.Payload.Length + inputs.Reference.Payload.Length, transmission.ScannedBytes);
            return new ValidatedAssessmentOutput(
                payload.Length,
                ProcessingIdentity.ComputePayloadSha256(payload),
                transmission.AlgorithmVersion,
                null,
                transmission.CoverageMillionths);
        }
        if (output is WeatherCloudOverlayResult rendered)
        {
            Assert.AreEqual(inputs.Width * inputs.Height *
                (inputs.PixelFormat == CameraPixelFormat.BayerRggb16 ? 3 : 1), rendered.Pixels.Length);
            return new ValidatedAssessmentOutput(
                rendered.Pixels.Length,
                ProcessingIdentity.ComputePayloadSha256(rendered.Pixels),
                rendered.AlgorithmVersion,
                null,
                null);
        }
        if (output is ProcessingOutcome outcome)
        {
            var product = AssertProduced(outcome);
            Assert.AreEqual(product.ChecksumSha256, ProcessingIdentity.ComputePayloadSha256(product.Payload));
            if (operation.StartsWith("assessment-host-", StringComparison.Ordinal))
            {
                var parsed = CloudAssessmentJson.Parse(product.Payload);
                Assert.IsTrue(parsed.Validation.IsValid, parsed.Validation.ReasonCode);
                Assert.IsNotNull(parsed.Assessment);
                CollectionAssert.AreEqual(
                    new[] { inputs.Current.ArtifactId, inputs.Reference.ArtifactId },
                    product.SourceArtifactIds.ToArray());
                return new ValidatedAssessmentOutput(
                    product.Payload.Length,
                    product.ChecksumSha256,
                    null,
                    product.OutputIdentitySha256,
                    parsed.Assessment.CoverageMillionths);
            }
            Assert.AreEqual(FrameArtifactRole.AnnotatedPreview, product.Role);
            Assert.HasCount(2, product.SourceArtifactIds);
            return new ValidatedAssessmentOutput(
                product.Payload.Length,
                product.ChecksumSha256,
                null,
                product.OutputIdentitySha256,
                null);
        }
        throw new InvalidOperationException($"Unsupported measured output '{output.GetType().Name}'.");
    }

    private static ProcessingProduct AssertProduced(ProcessingOutcome outcome)
    {
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        Assert.ContainsSingle(outcome.Products);
        return outcome.Products[0];
    }

    private static ProcessingExecutionRequest CreateAssessmentRequest(
        AssessmentInputs inputs,
        bool includeMask) => new(
        BuiltInProcessingRecipes.CloudAssessment,
        JsonSerializer.SerializeToElement(new CloudAssessmentOptions(
            MinimumReferenceSignal: 1,
            IncludeMask: includeMask)),
        ProcessingInputSelector.Raw("current"),
        [inputs.Current, inputs.Reference],
        "cloud-assessment-v1",
        AuxiliaryInputs:
        [
            new ProcessingAuxiliaryInput(
                "clear-reference",
                ProcessingAuxiliaryInputKind.Artifact,
                ProcessingInputSelector.Raw("reference"),
                ArtifactId: inputs.Reference.ArtifactId),
            inputs.EnvironmentInput
        ],
        InputArtifactId: inputs.Current.ArtifactId);

    private static Linear16CloudFrame CreateLinearFrame(ProcessingArtifact artifact)
    {
        var layout = artifact.Layout!;
        return new Linear16CloudFrame(
            new ImageLayout(layout.Width, layout.Height, layout.PixelFormat, layout.StrideBytes),
            artifact.Payload,
            checked((ushort)layout.BlackLevel!.Value),
            checked((ushort)layout.WhiteLevel!.Value));
    }

    private static ProcessingArtifact ToProcessingArtifact(
        ProcessingProduct product,
        DateTimeOffset capturedAtUtc) => new(
        ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
        product.Role,
        product.Variant,
        product.Recipe.IdentitySha256,
        product.MediaType,
        product.Layout,
        product.Payload,
        capturedAtUtc,
        product.TotalIntegration,
        product.Compatibility,
        SourceArtifactIds: product.SourceArtifactIds);

    private static string[] CreateOverlayLabels(CloudAssessmentV1 assessment)
    {
        var coverage = assessment.CoverageMillionths is { } value
            ? string.Create(CultureInfo.InvariantCulture, $"Cloud {value / 10_000}.{value % 10_000 / 1_000}%")
            : "Cloud unavailable";
        return
        [
            coverage,
            $"Quality {assessment.Quality}",
            $"Precipitation {assessment.Environment.PrecipitationStatus}"
        ];
    }

    private static object CreatePerformanceInterpretation(
        IReadOnlyList<AssessmentPerformanceMeasurement> measurements)
    {
        object Summarize(string workload)
        {
            var host = measurements.Where(item => item.Workload == workload
                && item.Operation.StartsWith("assessment-host-", StringComparison.Ordinal)).ToArray();
            var overlay = measurements.Where(item => item.Workload == workload
                && item.Operation == "overlay-host").ToArray();
            return new
            {
                assessmentMedianRangeMilliseconds = new[] { host.Min(item => item.MedianMilliseconds), host.Max(item => item.MedianMilliseconds) },
                assessmentP95MaximumMilliseconds = host.Max(item => item.P95Milliseconds),
                assessmentAllocatedBytesRange = new[] { host.Min(item => item.AllocatedBytesPerOperation), host.Max(item => item.AllocatedBytesPerOperation) },
                overlayMedianRangeMilliseconds = new[] { overlay.Min(item => item.MedianMilliseconds), overlay.Max(item => item.MedianMilliseconds) },
                overlayP95MaximumMilliseconds = overlay.Max(item => item.P95Milliseconds),
                overlayAllocatedBytesRange = new[] { overlay.Min(item => item.AllocatedBytesPerOperation), overlay.Max(item => item.AllocatedBytesPerOperation) },
                maximumPeakWorkingSetDeltaBytes = measurements.Where(item => item.Workload == workload)
                    .Max(item => item.PeakWorkingSetDeltaBytes),
                maximumRetainedManagedGrowthBytes = measurements.Where(item => item.Workload == workload)
                    .Max(item => item.ManagedLiveGrowthBytes)
            };
        }
        return new
        {
            W1 = Summarize("W1"),
            W2 = Summarize("W2"),
            interpretation = "Assessment is a net-new two-frame fixed-grid regression path. Compare its absolute cost to image-quality and no-op floors, not as equivalent work. Pure/host deltas expose orchestration and canonical JSON cost. CPU is runner-inclusive process CPU and is not attributed solely to recipe code. Overlay allocations are reported explicitly from isolated processes; no pooling or SIMD complexity was introduced without a measured follow-up benefit.",
            residualRisk = "At the default 85% transmission threshold, the fixed #104 partial and overcast fixtures both classify every supported grid cell as cloudy; coverage therefore does not distinguish their severity. Packed overlay composes through existing annotation renderers and allocates multiple full-dimension buffers; exact copies, post-GC LOH size, GC, and process-lifetime RSS peak are recorded for future optimization."
        };
    }

    private static void ValidateMeasurementMatrix(
        IReadOnlyList<AssessmentPerformanceMeasurement> measurements)
    {
        Assert.HasCount(36, measurements);
        foreach (var workload in new[] { "W1", "W2" })
        {
            foreach (var scenario in new[] { "clear", "partial", "overcast" })
            {
                var cases = measurements.Where(item => item.Workload == workload && item.Scenario == scenario)
                    .ToDictionary(item => item.Operation, StringComparer.Ordinal);
                Assert.HasCount(6, cases);
                var expectedCoverage = scenario == "clear" ? 0 : 1_000_000;
                Assert.AreEqual(expectedCoverage, cases["estimator-mask-on"].CoverageMillionths);
                Assert.AreEqual(expectedCoverage, cases["estimator-mask-off"].CoverageMillionths);
                Assert.AreEqual(expectedCoverage, cases["assessment-host-mask-on"].CoverageMillionths);
                Assert.AreEqual(expectedCoverage, cases["assessment-host-mask-off"].CoverageMillionths);
                Assert.AreEqual(cases["renderer"].OutputChecksumSha256, cases["overlay-host"].OutputChecksumSha256);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "global.json"))
                && File.Exists(Path.Combine(current.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("Unable to locate the repository root.");
    }

    private static string ReadGit(string repositoryRoot, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git failed: {error}");
        }
        return output.Trim();
    }

    private static string ReadProcessorName()
    {
        const string cpuInfo = "/proc/cpuinfo";
        if (File.Exists(cpuInfo))
        {
            var model = File.ReadLines(cpuInfo)
                .FirstOrDefault(static line => line.StartsWith("model name", StringComparison.Ordinal));
            if (model?.Split(':', 2) is [_, var value])
            {
                return value.Trim();
            }
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
            ?? RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static string GetRequiredEnvironment(string name)
        => Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Environment variable '{name}' is required.");

    private static Guid CreateGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private sealed record AssessmentInputs(
        string Workload,
        int Width,
        int Height,
        CameraPixelFormat PixelFormat,
        string VirtualScenario,
        ProcessingArtifact Current,
        ProcessingArtifact Reference,
        ProcessingAuxiliaryInput EnvironmentInput,
        string CurrentChecksumSha256,
        string ReferenceChecksumSha256);

    private sealed record ScenarioCapture(CameraModuleConfig Config, CameraFrame Frame);

    private sealed record MeasuredAssessmentOperation(
        string Name,
        long SourceBytes,
        int MaximumLiveFullFrameBuffers,
        int DeclaredFullFrameCopies,
        string Complexity,
        Func<ValueTask<object>> Execute);

    private sealed record ValidatedAssessmentOutput(
        long OutputBytes,
        string ChecksumSha256,
        string? AlgorithmVersion,
        string? OutputIdentitySha256,
        int? CoverageMillionths);

    private sealed record MeasurementLoopResult(
        double[] ElapsedMilliseconds,
        double CpuMilliseconds,
        long AllocatedBytes,
        ValidatedAssessmentOutput Validated);

    private sealed record AssessmentPerformanceMeasurement(
        string Workload,
        string Scenario,
        string VirtualScenario,
        string Operation,
        int Width,
        int Height,
        string PixelFormat,
        int Warmups,
        int Repetitions,
        long SourceBytes,
        long OutputBytes,
        double MedianMilliseconds,
        double P95Milliseconds,
        double CpuMillisecondsTotal,
        double CpuMillisecondsPerOperation,
        double AllocatedBytesPerOperation,
        long WorkingSetStartBytes,
        long WorkingSetEndBeforeCollectionBytes,
        long WorkingSetEndBytes,
        long PeakWorkingSetBytes,
        long PeakWorkingSetDeltaBytes,
        long ManagedLiveStartBytes,
        long ManagedLiveEndBytes,
        long ManagedLiveGrowthBytes,
        long PostGcLohStartBytes,
        long PostGcLohEndBytes,
        long PostGcLohGrowthBytes,
        long PostGcLohFragmentationStartBytes,
        long PostGcLohFragmentationEndBytes,
        int Generation0Collections,
        int Generation1Collections,
        int Generation2Collections,
        double OperationsPerSecond,
        double SourceMiBPerSecond,
        string OutputChecksumSha256,
        string? AlgorithmVersion,
        string? OutputIdentitySha256,
        int? CoverageMillionths,
        string CurrentInputChecksumSha256,
        string ReferenceInputChecksumSha256,
        int MaximumLiveFullFrameBuffers,
        int DeclaredFullFrameCopies,
        string Complexity,
        int FilesystemOperations,
        int SqliteOperations,
        int SqlOperations,
        int MinioOperations,
        int NetworkOperations,
        int DurableBacklogCount,
        long DurableBacklogBytes);
}
