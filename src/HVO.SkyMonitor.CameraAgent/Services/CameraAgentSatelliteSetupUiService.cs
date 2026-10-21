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
internal sealed record SatelliteTimeFileView(string Title, string? Path, string? Sha256, string Status);
internal sealed record SatelliteSetupView(IReadOnlyList<CameraAgentSettingsGroupState> Groups,
    IReadOnlyList<SatelliteSetupProblem> Problems, IReadOnlyList<SatelliteTimeFileView> Files,
    OrbitalUtcInterval? EarthOrientationCoverage, DateTimeOffset? LeapSecondsExpireUtc);

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
        var time = await timeSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        var files = new[]
        {
            await InspectAsync("Earth orientation", configuration.EarthOrientationPath, configuration.EarthOrientationSha256,
                CameraAgentPinnedOrbitalTimeSource.MaximumEarthOrientationBytes, time?.EarthOrientationSha256, cancellationToken).ConfigureAwait(false),
            await InspectAsync("Leap seconds", configuration.LeapSecondsPath, configuration.LeapSecondsSha256,
                CameraAgentPinnedOrbitalTimeSource.MaximumLeapSecondsBytes, time?.LeapSecondsSha256, cancellationToken).ConfigureAwait(false)
        };
        if (files.Any(file => file.Status == "Hash mismatch"))
            problems.Add(new("hash-mismatch", "A time-data file does not match its configured SHA-256.", TimeDataLink, "Correct the pinned time files"));
        else if (time is null || time.NominalZeroEarthOrientation)
            problems.Add(new("no-time-data", configuration.Enabled
                ? "Pinned actual Earth-orientation or leap-second data is missing, unreadable or invalid."
                : "Time data has not been resolved while planning is disabled.", TimeDataLink, "Configure time data"));
        else
        {
            try
            {
                _ = time.Evaluate(startUtc.ToUniversalTime());
                _ = time.Evaluate(endUtc.ToUniversalTime());
                time.ValidatePropagationInterval(startUtc.ToUniversalTime(), new(startUtc.ToUniversalTime(), endUtc.ToUniversalTime()));
            }
            catch (ArgumentException)
            {
                problems.Add(new("coverage-expired", "Time-data coverage is expired or does not cover the selected window; gaps and leap crossings are unsupported.",
                    TimeDataLink, "Update time data or change the planning window"));
            }
        }
        return OperatorUiResult<SatelliteSetupView>.Success(new(groups, problems, files,
            time?.EarthOrientationCoverage, time?.LeapTableExpiresUtc));
    }

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
