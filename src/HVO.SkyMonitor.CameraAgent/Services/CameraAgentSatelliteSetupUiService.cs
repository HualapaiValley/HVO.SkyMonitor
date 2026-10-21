using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed record SatelliteSetupProblem(string Code, string Message, string Link, string Action);
internal sealed record SatelliteTimeFileView(string Title, string? Path, string? Sha256, string Status, string? Origin = null);
internal sealed record SatelliteSetupView(IReadOnlyList<CameraAgentSettingsGroupState> Groups,
    IReadOnlyList<SatelliteSetupProblem> Problems, IReadOnlyList<SatelliteTimeFileView> Files,
    OrbitalUtcInterval? EarthOrientationCoverage, DateTimeOffset? LeapSecondsExpireUtc,
    string? ActiveSource = null, OrbitalUtcInterval? FinalCoverage = null, OrbitalUtcInterval? PredictedCoverage = null,
    OrbitalEarthOrientationStatus? WindowEarthOrientation = null);

internal interface ICameraAgentSatelliteSetupUiService
{
    Task<OperatorUiResult<SatelliteSetupView>> GetAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken);
    Task<OperatorUiResult<CameraAgentSettingsWriteResult>> SetAsync(string group, string version, string value, CancellationToken cancellationToken);
}

/// <summary>Authenticated host-only setup guidance. Existing stores and the pinned time loader retain all authority.</summary>
internal sealed class CameraAgentSatelliteSetupUiService(
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    CameraAgentSettingsService settings,
    ICameraAgentOrbitalUiService orbital,
    ICameraAgentOrbitalTimeSource timeSource,
    IOptionsMonitor<CameraAgentSatellitePlanningOptions> options) : ICameraAgentSatelliteSetupUiService
{
    internal const string TimeDataLink = "/operations/site#satellite-time-data";

    public async Task<OperatorUiResult<SatelliteSetupView>> GetAsync(DateTimeOffset startUtc, DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        var user = (await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        if (!(await authorization.AuthorizeAsync(user, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false)).Succeeded)
            return OperatorUiResult<SatelliteSetupView>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");
        var canMutate = (await authorization.AuthorizeAsync(user, null,
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false)).Succeeded;
        var groups = new List<CameraAgentSettingsGroupState>();
        foreach (var name in new[] { CameraAgentSettingsCatalogue.OrbitalElementsGroup, CameraAgentSettingsCatalogue.SatellitePlanningGroup })
        {
            var group = (await settings.GetGroupAsync(name, cancellationToken).ConfigureAwait(false))!;
            if (!canMutate)
                group = group with { CanWrite = false, ReadOnlyReason = "Operations change rights are required." };
            groups.Add(group);
        }
        var configuration = options.CurrentValue;
        var problems = new List<SatelliteSetupProblem>();
        if (!configuration.Enabled)
            problems.Add(new("disabled", "Satellite planning is disabled.", "/operations/site#satellitePlanning-switch", "Turn on satellite planning"));
        var snapshot = await orbital.GetAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is not { IsSuccess: true, Value: { HasData: true, Manifest: not null, StoreUnavailable: false } })
            problems.Add(new("no-snapshot", "No validated local orbital snapshot is available.", "/operations/software#software-orbital-heading", "Set up orbital refresh"));
        var resolution = await timeSource.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var time = resolution.Data;
        var bundled = resolution.Source == CameraAgentOrbitalTimeDataSource.Bundled;
        var files = bundled
            ? new[]
            {
                Bundled("Earth orientation", resolution.BundledEarthOrientation, resolution.Problem),
                Bundled("Leap seconds", resolution.BundledLeapSeconds, resolution.Problem)
            }
            : new[]
            {
                await InspectAsync("Earth orientation", configuration.EarthOrientationPath, configuration.EarthOrientationSha256,
                    CameraAgentPinnedOrbitalTimeSource.MaximumEarthOrientationBytes, time?.EarthOrientationSha256, cancellationToken).ConfigureAwait(false),
                await InspectAsync("Leap seconds", configuration.LeapSecondsPath, configuration.LeapSecondsSha256,
                    CameraAgentPinnedOrbitalTimeSource.MaximumLeapSecondsBytes, time?.LeapSecondsSha256, cancellationToken).ConfigureAwait(false)
            };
        OrbitalEarthOrientationStatus? window = null;
        if (files.Any(file => file.Status == "Hash mismatch"))
            problems.Add(new("hash-mismatch", "A time-data file does not match its configured SHA-256.", TimeDataLink, "Correct the pinned time files"));
        else if (time is null || time.NominalZeroEarthOrientation)
            problems.Add(new("no-time-data", !configuration.Enabled ? "Time data has not been resolved while planning is disabled." : resolution.Problem switch
            {
                CameraAgentOrbitalTimeDataProblem.BundledInvalid =>
                    "The bundled time-data pair is missing or failed verification. Reinstall CameraAgent, or configure a complete override.",
                CameraAgentOrbitalTimeDataProblem.OverrideIncomplete =>
                    "The time-data override is incomplete. Set both absolute paths and both SHA-256 values, or remove all four to use the bundled pair.",
                _ => "The configured time-data override is missing, unreadable or invalid. The bundled pair is not used while an override is configured."
            }, TimeDataLink, "Configure time data"));
        else
        {
            var start = startUtc.ToUniversalTime();
            var end = endUtc.ToUniversalTime();
            try
            {
                var first = time.Evaluate(start).Status;
                var last = time.Evaluate(end).Status;
                time.ValidatePropagationInterval(start, new(start, end));
                window = first == last ? first : OrbitalEarthOrientationStatus.Mixed;
            }
            catch (ArgumentException)
            {
                problems.Add(new("coverage-expired", CameraAgentSatellitePlanner.CoverageEndsBefore(time, end) is { } until
                    ? $"{(bundled ? "The bundled time data" : "The configured override")} covers planning only until {until:O} " +
                      $"(Earth orientation to {time.EarthOrientationCoverage?.EndUtc:O}; leap-second list expires {time.LeapTableExpiresUtc:O}). " +
                      "Update CameraAgent for a newer bundled pair, or configure a newer override."
                    : "Time data does not cover the selected window. Gaps between final and predicted Earth orientation and leap-second crossings are unsupported.",
                    TimeDataLink, "Update time data or change the planning window"));
            }
        }
        return OperatorUiResult<SatelliteSetupView>.Success(new(groups, problems, files,
            time?.EarthOrientationCoverage, time?.LeapTableExpiresUtc,
            bundled ? "Bundled with this CameraAgent release" : "Configured override",
            time?.EarthOrientationFinalCoverage, time?.EarthOrientationPredictedCoverage, window));
    }

    private static SatelliteTimeFileView Bundled(string title, CameraAgentBundledTimeFile? file, CameraAgentOrbitalTimeDataProblem problem)
        => file is null
            ? new(title, null, null, problem == CameraAgentOrbitalTimeDataProblem.BundledInvalid ? "Bundled pair failed verification" : "Bundled; not resolved")
            : new(title, Path.Combine(CameraAgentPinnedOrbitalTimeSource.BundledDirectory, file.File), file.Sha256, "Bundled and hash verified",
                $"Retrieved {file.RetrievedUtc:O} from {file.SourceUrl}. {file.Terms}");

    public async Task<OperatorUiResult<CameraAgentSettingsWriteResult>> SetAsync(string group, string version, string value,
        CancellationToken cancellationToken)
    {
        var user = (await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        var actor = CameraAgentCredentialAccess.GetOwnerId(user);
        if (string.IsNullOrWhiteSpace(actor) || !(await authorization.AuthorizeAsync(user, null,
                CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false)).Succeeded)
            return OperatorUiResult<CameraAgentSettingsWriteResult>.Failure(OperatorUiResultKind.Unauthorized, "Operations change rights are required.");
        if (group is not (CameraAgentSettingsCatalogue.OrbitalElementsGroup or CameraAgentSettingsCatalogue.SatellitePlanningGroup))
            return OperatorUiResult<CameraAgentSettingsWriteResult>.Failure(OperatorUiResultKind.Invalid, "Unknown feature switch.");
        var result = await settings.WriteAsync(group, new(version,
            new Dictionary<string, JsonElement> { [group + ".enabled"] = JsonSerializer.SerializeToElement(value) }),
            actor, null, cancellationToken).ConfigureAwait(false);
        return OperatorUiResult<CameraAgentSettingsWriteResult>.Success(result);
    }

    private static async Task<SatelliteTimeFileView> InspectAsync(string title, string? path, string? hash, int limit,
        string? resolvedHash, CancellationToken cancellationToken)
    {
        var status = "Not configured";
        if (resolvedHash is not null) status = "Resolved and hash verified";
        else if (Path.IsPathFullyQualified(path ?? "") && hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit))
        {
            try
            {
                using var stream = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length is <= 0 || stream.Length > limit) status = "Invalid size";
                else
                {
                    var bytes = new byte[(int)stream.Length];
                    await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                    status = string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash, StringComparison.OrdinalIgnoreCase)
                        ? "Hash verified; not resolved" : "Hash mismatch";
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                status = "Unreadable";
            }
        }
        return new(title, path, hash, status);
    }
}
