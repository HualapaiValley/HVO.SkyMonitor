using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

namespace HVO.SkyMonitor.VirtualSkyPoc;

internal static partial class Program
{
    // Offline canonical snapshot inputs only. Acquisition/GP normalization is owned by #528.
    private static async Task OrbitalPredictPoc(string[] args)
    {
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new ArgumentException("Use a new empty output directory; retained evidence is immutable.");
        var config = JsonSerializer.Deserialize<CameraModuleConfig>(ReadSatelliteInput(args[2]), Json)
            ?? throw new ArgumentException("Missing camera settings.");
        var parsed = OrbitalSnapshot.Parse(ReadOrbitalPocInput(args[3], OrbitalContract.MaximumManifestBytes),
            ReadOrbitalPocInput(args[4], OrbitalContract.MaximumPayloadBytes));
        var snapshot = parsed.Snapshot ?? throw new ArgumentException($"Invalid canonical snapshot: {parsed.Reason}");
        var timeData = OrbitalTimeData.FromFinals2000A(ReadOrbitalPocInput(args[5], 8 * 1024 * 1024), ReadOrbitalPocInput(args[6], 64 * 1024));
        var identity = new OrbitalObjectIdentity("norad", args[7]);
        if (!OrbitalContract.IsObjectIdentity(identity) || snapshot.RecordsFor(identity).Count == 0)
            throw new ArgumentException("Select an object present in the retained snapshot.");
        var start = DateTimeOffset.Parse(args[8], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var seconds = double.Parse(args[9], CultureInfo.InvariantCulture);
        if (start.Offset != TimeSpan.Zero || !double.IsFinite(seconds) || seconds is <= 0 or > 20)
            throw new ArgumentException("UTC start and an explicit 0..20-second duration are required.");
        Directory.CreateDirectory(output);
        var rigHash = RigProjectionContextFactory.CreateProfileHashSha256(config.Rig).ToUpperInvariant();
        var readout = SatelliteReadout(config);
        var readoutHash = CaptureContractJson.ComputeCanonicalJsonSha256(readout);
        var projection = RigProjectionContextFactory.Create(config.Rig);
        var calibration = new AstrometricCalibration(projection, config.Rig.Optics.CalibrationVersion, readoutHash);
        var geometry = new OrbitalGeometryFacts(OrbitalGeometryBasis.ConfiguredRig, rigHash, readoutHash, readout,
            calibration.IdentitySha256.ToUpperInvariant(), null, null, OrbitalProjectionCoordinates.ReadoutView, projection,
            ProjectedSceneImageTransformV1.Identity(projection.WidthPixels, projection.HeightPixels), HorizonPolicy.GeometricHorizon);
        var observer = new ObserverLocation(config.Observatory.LatitudeDegrees, config.Observatory.LongitudeDegrees, config.Observatory.ElevationMeters);
        var policy = new OrbitalSelectionPolicy(snapshot.Manifest.Sources.Select(static s => new OrbitalSourcePriority(s.DatasetKey, 0)));
        var model = OrbitalPredictionModel.Create(timeData, assumeEllipsoidalHeight: true);
        var bounds = new OrbitalQueryBounds(1, 100, 100000, 1, 100, 10000, 4000, 1, .01);
        var engine = new OrbitalPredictionEngine(timeData);
        var measurements = new List<object>();
        using var process = Process.GetCurrentProcess();
        for (var frame = 0; frame < 5; frame++)
        {
            var interval = new OrbitalUtcInterval(start.AddSeconds(frame * seconds), start.AddSeconds((frame + 1) * seconds));
            var timing = new OrbitalTimingFacts(interval, OrbitalTimingBasis.Estimated, null, OrbitalTimingBasis.Unknown,
                "virtual-poc-logical-shutter-estimated; not sensor-actual", AstrometricClockFacts.NotSupplied);
            var query = new OrbitalExposureQuery(new(OrbitalPredictionUse.Diagnostic, snapshot, policy, interval, [identity], observer, model, bounds), geometry, timing);
            for (var run = 0; run < 2; run++)
            {
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var allocated = GC.GetTotalAllocatedBytes();
                var timer = Stopwatch.StartNew();
                var result = await engine.PredictExposureAsync(query, CancellationToken.None).ConfigureAwait(false);
                var elapsed = timer.Elapsed.TotalMilliseconds;
                process.Refresh();
                var allocations = GC.GetTotalAllocatedBytes() - allocated;
                var cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                WriteJson(Path.Combine(output, $"result-{frame + 1:00}-{run}.json"), result);
                if (result.Status != OrbitalExecutionStatus.Complete || result.Products.Count != 1)
                    throw new InvalidOperationException($"Frame {frame + 1} did not complete: {result.Reason}; {string.Join(';', result.Failures.Select(static f => f.Detail))}");
                var bytes = OrbitalPredictionJson.SerializeTrack(result.Products[0]);
                var path = Path.Combine(output, $"track-{frame + 1:00}.json");
                if (run == 0) await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
                else if (!(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes))
                    throw new InvalidOperationException("Repeated immutable inputs produced different canonical track bytes.");
                measurements.Add(new
                {
                    frame = frame + 1,
                    run,
                    elapsedMillisecondsIncludingResultConstruction = elapsed,
                    cpuMilliseconds,
                    allocatedBytes = allocations,
                    process.WorkingSet64,
                    process.PeakWorkingSet64,
                    result.EvaluatedObjects,
                    result.PropagationEvaluations,
                    result.ElapsedMilliseconds,
                    samples = result.Products[0].Samples.Count,
                    result.Products[0].Sampling,
                    result.Products[0].ProductSha256
                });
            }
        }
        WriteIdentity(output);
        WriteJson(Path.Combine(output, "prediction-evidence.json"), new
        {
            schema = "orbital-prediction-poc-v1",
            snapshot.Manifest.ManifestSha256,
            snapshot.Manifest.NormalizedPayloadSha256,
            model,
            observer,
            geometry,
            policy = policy.IdentitySha256,
            bounds,
            measurements,
            inputs = args.Skip(2).Take(5).Select(static p => new { path = Path.GetFullPath(p), sha256 = SatelliteHash(File.ReadAllBytes(p)) }),
            limitation = "Component engineering evidence; first-query vs repeat is not isolated cold/warm host qualification. Synthetic optical behavior is separate. No physical accuracy, brightness or integrated cadence claim."
        });
        Console.WriteLine($"Wrote five canonical mode-a tracks and matching repeated-input evidence to {output}.");
    }

    private static byte[] ReadOrbitalPocInput(string path, int maximumBytes)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 || stream.Length > maximumBytes) throw new ArgumentException("Orbital input exceeds its bound.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new ArgumentException("Orbital input changed during reading.");
        return bytes;
    }
}
