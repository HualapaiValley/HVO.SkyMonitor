using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>
/// Read-only configured-rig diagnostics over immutable inputs. All work, including synchronous engine
/// ValueTasks, runs outside the caller's circuit. One request is admitted; cancellation is awaited,
/// never detached. Retention bounds source bytes and product counts, not exact managed working set.
/// </summary>
public sealed class CameraAgentSatellitePlanner(
    ICameraAgentSkyMapProjection skyMap,
    ICameraAgentConfigurationAccessor configuration,
    ICameraAgentOrbitalTimeSource timeSource,
    ICameraAgentOrbitalPlanningEngineFactory engineFactory,
    IOptionsMonitor<CameraAgentSatellitePlanningOptions> options,
    TimeProvider timeProvider,
    IOrbitalSnapshotReader? snapshotReader = null,
    CaptureScheduleRuntimeCoordinator? scheduleRuntime = null) : ICameraAgentSatellitePlanner
{
    public const int MaximumObjects = 32;
    public const int MaximumPasses = 128;
    public const int MaximumSessions = 4;
    public const long MaximumRetainedBytes = 128L * 1024 * 1024;
    public static TimeSpan SessionLifetime { get; } = TimeSpan.FromMinutes(10);
    public static TimeSpan MaximumWindow { get; } = TimeSpan.FromHours(24);
    public static OrbitalQueryBounds SearchBounds { get; } = new(MaximumObjects, 86400, 200_000,
        MaximumObjects, MaximumPasses, 16_384, 1500, 1, .25);
    public static OrbitalPassArcBounds ArcBounds { get; } = new(100_000, OrbitalPassArcJson.MaximumMaterializedSamples, 1500, 1, .01);

    private const string Limitations = "Configured-rig diagnostics; height datum assumed ellipsoidal; physical orbit, clock and calibration uncertainty unqualified; illumination and brightness unknown. " +
        "Limits: 24 hours, 32 objects, 128 passes; search/arc 1500 ms each, 200000/100000 evaluations respectively; arc 8064 samples, step 1 s and angular tolerance 0.01 degrees. Source population coverage is independent of numerical completion.";
    private readonly List<Session> sessions = [];
    private int active;

    public ValueTask<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>> SearchAsync(
        CameraAgentSatellitePassSearchRequest request, CancellationToken cancellationToken)
        => RunAsync(() => SearchCoreAsync(request, cancellationToken), cancellationToken);

    public ValueTask<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>> GetArcAsync(
        string searchIdentitySha256, string parentPassSha256, CancellationToken cancellationToken)
        => RunAsync(() => ArcCoreAsync(searchIdentitySha256, parentPassSha256, cancellationToken), cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Availability boundary must sanitize adapter failures; caller cancellation is rethrown.")]
    private async ValueTask<CameraAgentSatellitePlanningResponse<T>> RunAsync<T>(
        Func<Task<CameraAgentSatellitePlanningResponse<T>>> work, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            return Response<T>(CameraAgentSatellitePlanningOutcome.Busy, "Another local planning request is running. Retry after it completes.");
        try
        {
            return await Task.Run(async () =>
            {
                try
                {
                    ExpireSessions();
                    var result = await work().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return result;
                }
                catch (PlanningUnavailable exception)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Response<T>(exception.Outcome, exception.Message);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Local paths, configuration details, credentials and provider exception text never enter DTOs.
                    return Response<T>(CameraAgentSatellitePlanningOutcome.Failed, "Local planning could not complete. No result was substituted.");
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref active, 0); }
    }

    private async Task<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>> SearchCoreAsync(
        CameraAgentSatellitePassSearchRequest request, CancellationToken cancellationToken)
    {
        if (request is null || request.StartUtc.Offset != TimeSpan.Zero || request.EndUtc.Offset != TimeSpan.Zero ||
            request.EndUtc <= request.StartUtc || request.EndUtc - request.StartUtc > MaximumWindow ||
            !double.IsFinite(request.MinimumElevationDegrees) || request.MinimumElevationDegrees is < 0 or >= 90 ||
            !OrbitalContract.IsSha256(request.ExpectedObserverSha256) || !OrbitalContract.IsSha256(request.ExpectedRigSha256))
            throw Invalid("Specify a positive UTC window of at most 24 hours, an elevation from 0 to below 90 degrees, and the displayed site/rig identities.");
        var filter = ParseFilter(request.ObjectFilter);
        var settings = options.CurrentValue.Copy();
        var settingsIdentity = Hash(settings);
        var time = await ReadTimeAsync(settings, cancellationToken).ConfigureAwait(false);
        var interval = new OrbitalUtcInterval(request.StartUtc, request.EndUtc);
        var model = OrbitalPredictionModel.Create(time, assumeEllipsoidalHeight: true);
        ValidateQueryTime(time, model, interval);
        var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var context = await ReadContextAsync(interval, cancellationToken).ConfigureAwait(false);
        if (context.Observer.CanonicalSha256 != request.ExpectedObserverSha256 || context.Rig.RigProfileHashSha256 != request.ExpectedRigSha256)
            throw ContextChanged();
        var matches = snapshot.ObjectIdentities.Where(identity => Matches(snapshot, identity, filter)).ToArray();
        if (matches.Length == 0)
            throw new PlanningUnavailable(CameraAgentSatellitePlanningOutcome.NoMatches,
                "No matching objects in this snapshot (matching 0; selected 0). This does not establish global absence.");
        var selected = matches.Take(MaximumObjects).ToArray();
        var policy = new OrbitalSelectionPolicy([new("celestrak:gp:active", 0)], settings.AllowStaleDiagnostics);
        ValidateSelectedEpochs(time, interval, snapshot, selected, policy);
        var query = new OrbitalPassQuery(new(OrbitalPredictionUse.Diagnostic, snapshot, policy, interval, selected,
            new(context.Observer.LatitudeDegrees, context.Observer.LongitudeDegrees, context.Observer.ElevationMeters),
            model, SearchBounds), request.MinimumElevationDegrees);
        var contextIdentity = Hash(context);
        var queryIdentity = Hash(new
        {
            Version = "cameraagent-satellite-planning-v1",
            Settings = settingsIdentity,
            Context = contextIdentity,
            Snapshot = snapshot.Manifest.ManifestSha256,
            Policy = policy.IdentitySha256,
            Model = model,
            Interval = interval,
            Filter = filter.Identity,
            Objects = query.Request.Objects,
            Bounds = SearchBounds,
            Arc = ArcBounds,
            request.MinimumElevationDegrees
        });
        var retainedBytes = snapshot.Manifest.Sources.Sum(static source => source.RawLengthBytes) +
            snapshot.Manifest.NormalizedPayloadLengthBytes + snapshot.SerializeManifest().Length +
            OrbitalContract.SerializeCanonical(context).Length + OrbitalContract.SerializeCanonical(model).Length;
        if (retainedBytes > MaximumRetainedBytes) throw RetentionExceeded();
        var engines = engineFactory.Create(time);
        var prediction = await engines.Prediction.FindPassesAsync(query, cancellationToken).ConfigureAwait(false);
        retainedBytes += prediction.Products.Sum(static pass => (long)OrbitalPredictionJson.SerializePass(pass).Length);
        if (retainedBytes > MaximumRetainedBytes) throw RetentionExceeded();
        await VerifyCurrentAsync(settingsIdentity, snapshot.Manifest.ManifestSha256, model, interval, contextIdentity,
            cancellationToken).ConfigureAwait(false);
        var rows = prediction.Products.OrderBy(static pass => pass.PassInterval.StartUtc)
            .ThenBy(static pass => pass.Lineage.ObjectIdentity.CatalogNumber, StringComparer.Ordinal)
            .ThenBy(static pass => pass.ProductSha256, StringComparer.Ordinal)
            .Select(pass => new CameraAgentSatellitePassRow(
                snapshot.RecordsFor(pass.Lineage.ObjectIdentity).Single(record => record.RecordSha256 == pass.Lineage.RecordSha256).ObjectName
                    ?? pass.Lineage.ObjectIdentity.CatalogNumber, pass)).ToArray();
        var now = timeProvider.GetUtcNow();
        // A later identical request must never resurrect an expired handle or overwrite another result prefix.
        var identity = Hash(new { QuerySha256 = queryIdentity, CreatedUtc = now, SessionNonce = Guid.NewGuid() });
        var view = new CameraAgentSatellitePassSearchView(identity, now, now.Add(SessionLifetime), context, prediction,
            Array.AsReadOnly(rows), matches.Length, selected.Length,
            snapshot.Manifest.Sources.Count == 0 ? null : snapshot.Manifest.Sources.Max(static source => source.RetrievedUtc));
        cancellationToken.ThrowIfCancellationRequested();
        ExpireSessions();
        // Oldest insertion wins eviction, including equal-clock requests. Never silently evict this candidate.
        while (sessions.Count >= MaximumSessions || sessions.Sum(static session => session.RetainedBytes) + retainedBytes > MaximumRetainedBytes)
            sessions.RemoveAt(0);
        sessions.Add(new(view, query, time, engines, settingsIdentity, contextIdentity, retainedBytes));
        var truncation = matches.Length > selected.Length ? " Object selection was truncated deterministically." : "";
        return new(CameraAgentSatellitePlanningOutcome.Ready,
            $"Matching objects: {matches.Length}; selected: {selected.Length}.{truncation} {Limitations}", view);
    }

    private async Task<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>> ArcCoreAsync(
        string searchIdentity, string parentIdentity, CancellationToken cancellationToken)
    {
        if (!OrbitalContract.IsSha256(searchIdentity) || !OrbitalContract.IsSha256(parentIdentity))
            throw Invalid("Specify the exact returned search and parent-pass identities.");
        var session = sessions.SingleOrDefault(item => item.View.SearchIdentitySha256 == searchIdentity)
            ?? throw new PlanningUnavailable(CameraAgentSatellitePlanningOutcome.Expired, "The original search is unavailable or expired. Run a new search.");
        var parent = session.View.Passes.SingleOrDefault(row => row.Pass.ProductSha256 == parentIdentity)?.Pass
            ?? throw Invalid("That parent pass was not returned by the retained original search.");
        await VerifyCurrentAsync(session.SettingsIdentity, session.Query.Request.Snapshot.Manifest.ManifestSha256,
            session.Query.Request.Model, session.Query.Request.Interval, session.ContextIdentity, cancellationToken).ConfigureAwait(false);
        // Only the original query and exact returned parent are admitted. The producer also validates their provenance.
        var arcQuery = new OrbitalPassArcQuery(session.Query, parent, ArcBounds);
        var prediction = await session.Engines.Arcs.PredictPassArcAsync(arcQuery, cancellationToken).ConfigureAwait(false);
        await VerifyCurrentAsync(session.SettingsIdentity, session.Query.Request.Snapshot.Manifest.ManifestSha256,
            session.Query.Request.Model, session.Query.Request.Interval, session.ContextIdentity, cancellationToken).ConfigureAwait(false);
        if (timeProvider.GetUtcNow() >= session.View.ExpiresUtc)
        {
            sessions.Remove(session);
            throw new PlanningUnavailable(CameraAgentSatellitePlanningOutcome.Expired, "The original search expired during detail generation. Run a new search.");
        }
        return new(CameraAgentSatellitePlanningOutcome.Ready, Limitations,
            new(searchIdentity, parentIdentity, session.View.Context, prediction));
    }

    private async Task VerifyCurrentAsync(string settingsIdentity, string snapshotIdentity, OrbitalPredictionModelFacts model,
        OrbitalUtcInterval interval, string contextIdentity, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue.Copy();
        if (Hash(settings) != settingsIdentity) throw ContextChanged();
        var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var time = await ReadTimeAsync(settings, cancellationToken).ConfigureAwait(false);
        if (snapshot.Manifest.ManifestSha256 != snapshotIdentity || OrbitalPredictionModel.Create(time, assumeEllipsoidalHeight: true) != model ||
            Hash(await ReadContextAsync(interval, cancellationToken).ConfigureAwait(false)) != contextIdentity)
            throw ContextChanged();
    }

    private async Task<OrbitalSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        if (snapshotReader is not null &&
            (await snapshotReader.ReadCurrentAsync(cancellationToken).ConfigureAwait(false)).Snapshot is { } snapshot) return snapshot;
        throw new PlanningUnavailable(CameraAgentSatellitePlanningOutcome.NoSnapshot, "No validated local orbital snapshot is available.");
    }

    private async Task<OrbitalTimeData> ReadTimeAsync(CameraAgentSatellitePlanningOptions settings, CancellationToken cancellationToken)
    {
        if (settings.Enabled && await timeSource.ReadAsync(cancellationToken).ConfigureAwait(false) is { NominalZeroEarthOrientation: false } time)
            return time;
        throw new PlanningUnavailable(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable,
            "Planning requires enabled, explicitly pinned actual Earth-orientation and leap-second inputs. No approximation was substituted.");
    }

    private async Task<CameraAgentSatellitePlanningContext> ReadContextAsync(OrbitalUtcInterval interval, CancellationToken cancellationToken)
    {
        if (!configuration.IsConfigured) throw ContextChanged();
        var startup = await configuration.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var before = scheduleRuntime?.Snapshot?.Configuration ?? startup;
        var beforeHash = RigProjectionContextFactory.CreateProfileHashSha256(before.Rig);
        var scene = await skyMap.ProjectAsync(interval.StartUtc, cancellationToken).ConfigureAwait(false);
        startup = await configuration.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var after = scheduleRuntime?.Snapshot?.Configuration ?? startup;
        var rigHash = RigProjectionContextFactory.CreateProfileHashSha256(after.Rig);
        var projection = RigProjectionContextFactory.Create(after.Rig);
        var geometry = scene.Geometry;
        if (beforeHash != rigHash || rigHash != geometry.RigProfileHashSha256 ||
            geometry.RigProfileVersion != after.Rig.ProfileVersion || geometry.ProjectionCalibrationVersion != after.Rig.Optics.CalibrationVersion ||
            geometry.ProjectionAlgorithmVersion != RigProjectionContextFactory.AlgorithmVersion ||
            geometry.ProjectionModel != projection.Model.ToString() || geometry.Aperture != projection.Aperture.ToString() ||
            geometry.BoresightAltitudeDegrees != projection.BoresightAltitudeDegrees || geometry.BoresightAzimuthDegrees != projection.BoresightAzimuthDegrees ||
            geometry.RollDegrees != projection.RollDegrees || geometry.HorizontalFlip != projection.HorizontalFlip ||
            geometry.FieldOfViewDegrees != after.Rig.Optics.FieldOfViewDegrees || geometry.VerticalFieldOfViewDegrees != after.Rig.Optics.VerticalFieldOfViewDegrees ||
            geometry.PrincipalPointX != projection.PrincipalPointX || geometry.PrincipalPointY != projection.PrincipalPointY ||
            geometry.FocalLengthXPixels != projection.FocalLengthXPixels || geometry.FocalLengthYPixels != projection.FocalLengthYPixels ||
            geometry.WidthPixels != projection.WidthPixels || geometry.HeightPixels != projection.HeightPixels ||
            geometry.ImageCircleRadiusPixels != projection.ImageCircleRadiusPixels || geometry.RadialDistortionK1 != projection.RadialDistortionK1 ||
            scene.AtUtc != interval.StartUtc || !scene.Observer.EffectiveAtInstant || interval.StartUtc < scene.Observer.EffectiveFromUtc ||
            scene.Observer.EffectiveUntilUtc is { } until && interval.EndUtc >= until)
            throw ContextChanged();
        // Preserve the authoritative facts, freezing nested collections so a response cannot mutate retained context.
        geometry = geometry with
        {
            Cardinals = Array.AsReadOnly(geometry.Cardinals.ToArray()),
            AltitudeRings = Array.AsReadOnly(geometry.AltitudeRings.Select(ring => ring with
            {
                Lines = Array.AsReadOnly(ring.Lines.Select(line => line with
                { Points = Array.AsReadOnly(line.Points.ToArray()) }).ToArray())
            }).ToArray())
        };
        return new(scene.Observer, geometry, projection);
    }

    private static void ValidateQueryTime(OrbitalTimeData time, OrbitalPredictionModelFacts model, OrbitalUtcInterval interval)
    {
        try
        {
            time.ValidateModel(model, interval, OrbitalPredictionUse.Diagnostic, OrbitalGeometryBasis.ConfiguredRig);
        }
        catch (ArgumentException)
        {
            throw TimeInputsUnavailable();
        }
    }

    private static void ValidateSelectedEpochs(OrbitalTimeData time, OrbitalUtcInterval interval,
        OrbitalSnapshot snapshot, OrbitalObjectIdentity[] objects, OrbitalSelectionPolicy policy)
    {
        try
        {
            foreach (var identity in objects)
                if (OrbitalRecordSelector.Select(snapshot, identity, interval, policy).SelectedRecord is { } record)
                    time.ValidatePropagationInterval(record.EpochUtc, interval);
        }
        catch (ArgumentException)
        {
            throw TimeInputsUnavailable();
        }
    }

    private static Filter ParseFilter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256 || text.Any(char.IsControl))
            throw Invalid("Specify a name or canonical catalog numbers in at most 256 characters.");
        text = text.Trim();
        if (text.All(static c => char.IsAsciiDigit(c) || c is ',' or ';' or ' '))
        {
            var ids = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            if (ids.Length is 0 or > MaximumObjects || ids.Any(static id => !OrbitalContract.IsObjectIdentity(new("norad", id))))
                throw Invalid("Specify at most 32 canonical catalog numbers without leading zeros.");
            return new(string.Join(',', ids), ids, null);
        }
        return new(text.ToUpperInvariant(), null, text);
    }

    private static bool Matches(OrbitalSnapshot snapshot, OrbitalObjectIdentity identity, Filter filter)
        => filter.CatalogNumbers is { } ids ? ids.Contains(identity.CatalogNumber, StringComparer.Ordinal) :
            snapshot.RecordsFor(identity).Any(record => record.ObjectName?.Contains(filter.Name!, StringComparison.OrdinalIgnoreCase) == true);

    private void ExpireSessions() => sessions.RemoveAll(session => timeProvider.GetUtcNow() >= session.View.ExpiresUtc);
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(OrbitalContract.SerializeCanonical(value)));
    private static PlanningUnavailable Invalid(string message) => new(CameraAgentSatellitePlanningOutcome.Invalid, message);
    private static PlanningUnavailable TimeInputsUnavailable() => new(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable,
        "Pinned time inputs do not continuously support the original interval and selected epochs, or cross an unsupported leap second.");
    private static PlanningUnavailable ContextChanged() => new(CameraAgentSatellitePlanningOutcome.ContextChanged,
        "The authoritative site, active rig, snapshot, time inputs or planning options changed. Refresh the scene and run a new search.");
    private static PlanningUnavailable RetentionExceeded() => Invalid("The original search exceeds the local 128 MiB source-byte/product retention cap and was not retained. This cap is not an exact managed-memory bound.");
    private static CameraAgentSatellitePlanningResponse<T> Response<T>(CameraAgentSatellitePlanningOutcome outcome, string message) where T : class
        => new(outcome, message, null);
    [SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Private control flow carrying only fixed sanitized messages; never exposed.")]
    [SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "Private availability control flow consumed at the facade boundary.")]
    private sealed class PlanningUnavailable(CameraAgentSatellitePlanningOutcome outcome, string message) : Exception(message)
    { public CameraAgentSatellitePlanningOutcome Outcome { get; } = outcome; }
    private sealed record Filter(string Identity, string[]? CatalogNumbers, string? Name);
    private sealed record Session(CameraAgentSatellitePassSearchView View, OrbitalPassQuery Query, OrbitalTimeData Time,
        CameraAgentOrbitalPlanningEngines Engines, string SettingsIdentity, string ContextIdentity, long RetainedBytes);
}
