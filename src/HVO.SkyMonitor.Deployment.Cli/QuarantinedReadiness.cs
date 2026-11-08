using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Decides when a CameraAgent <c>/health</c> report that is Unhealthy may still be treated as ready for backup and
/// upgrade. The only accepted cause is a required processing-lane quarantine, optionally together with the terminal
/// processing outcome that quarantine records. Every other unhealthy check still fails readiness. Catalog identity is
/// required either way, and this decision never inspects an archive.
/// </summary>
internal static class QuarantinedReadiness
{
    internal const string LaneCheck = "capture-lanes";
    internal const string ProcessingCheck = "capture-processing";
    internal const string LaneDescription = "A required capture lane cannot safely accept work.";
    internal const string ProcessingDescription = "Capture processing has a required terminal graph outcome.";

    /// <summary>
    /// The catalog check the installer already required of a successful <c>/health</c>: the selected catalog's identity,
    /// and its package version when the host reports one.
    /// </summary>
    internal static bool CatalogMatches(JsonElement health, ApprovedCatalogContract expectedCatalog)
    {
        var catalog = health.GetProperty("checks").EnumerateArray().Single(item =>
            item.GetProperty("name").GetString() == "catalog");
        var data = catalog.GetProperty("data");
        return data.GetProperty("CatalogId").GetString() == expectedCatalog.CatalogId &&
               data.GetProperty("CatalogVersion").GetString() == expectedCatalog.CatalogVersion &&
               data.GetProperty("SchemaVersion").GetString() == expectedCatalog.SchemaVersion &&
               data.GetProperty("PreprocessingVersion").GetString() == expectedCatalog.PreprocessingVersion &&
               data.GetProperty("RowCount").GetInt64() == expectedCatalog.RowCount &&
               (!data.TryGetProperty("PackageVersion", out var packageVersion) ||
                expectedCatalog.IsPackageVersion(packageVersion.GetString()));
    }

    /// <summary>
    /// True when <paramref name="health"/> is Unhealthy only because of a processing-lane quarantine and its catalog
    /// identity matches <paramref name="expectedCatalog"/>. <paramref name="reason"/> says which check prevented it.
    /// </summary>
    internal static bool TryAccept(JsonElement health, ApprovedCatalogContract expectedCatalog, out string reason)
    {
        try
        {
            if (health.ValueKind != JsonValueKind.Object ||
                health.GetProperty("status").GetString() != "Unhealthy" ||
                health.GetProperty("checks").ValueKind != JsonValueKind.Array)
            {
                reason = "CameraAgent /health was not an Unhealthy readiness report.";
                return false;
            }

            if (!CatalogMatches(health, expectedCatalog))
            {
                reason = "CameraAgent catalog identity does not match the instance.";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var laneQuarantine = false;
            long requiredProcessingQuarantine = 0;
            long? processingTerminal = null;
            string? processingProblem = null;
            var otherUnhealthy = new List<string>();
            foreach (var check in health.GetProperty("checks").EnumerateArray())
            {
                var name = check.GetProperty("name").GetString();
                var status = check.GetProperty("status").GetString();
                if (string.IsNullOrEmpty(name) || !seen.Add(name) ||
                    status is not ("Healthy" or "Degraded" or "Unhealthy"))
                {
                    reason = "CameraAgent /health could not be read as a readiness report.";
                    return false;
                }

                if (name == "catalog" && status != "Healthy")
                {
                    reason = "CameraAgent catalog check is not Healthy.";
                    return false;
                }

                if (status != "Unhealthy")
                {
                    continue;
                }

                if (name == LaneCheck)
                {
                    if (!IsLaneQuarantine(check, out requiredProcessingQuarantine, out var laneProblem))
                    {
                        reason = laneProblem;
                        return false;
                    }

                    laneQuarantine = true;
                    continue;
                }

                if (name == ProcessingCheck)
                {
                    if (!IsProcessingTerminal(check, out var terminal, out var problem))
                    {
                        processingProblem = problem;
                    }
                    else
                    {
                        processingTerminal = terminal;
                    }

                    continue;
                }

                otherUnhealthy.Add(name);
            }

            if (!laneQuarantine)
            {
                reason = "CameraAgent /health is unhealthy for a reason other than a processing-lane quarantine.";
                return false;
            }

            if (processingProblem is not null)
            {
                reason = processingProblem;
                return false;
            }

            if (processingTerminal is { } terminalCount && terminalCount != requiredProcessingQuarantine)
            {
                reason = "capture-processing terminal evidence does not match the required processing-lane quarantine.";
                return false;
            }

            if (otherUnhealthy.Count > 0)
            {
                reason = "CameraAgent /health has unhealthy checks other than the processing-lane quarantine: " +
                         string.Join(", ", otherUnhealthy);
                return false;
            }

            reason = "";
            return true;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            reason = "CameraAgent /health could not be read as a readiness report.";
            return false;
        }
    }

    private static bool IsLaneQuarantine(JsonElement check, out long processingQuarantine, out string reason)
    {
        processingQuarantine = 0;
        reason = "capture-lanes is unhealthy for a reason other than a processing-lane quarantine.";
        if (check.GetProperty("description").GetString() != LaneDescription ||
            !check.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            data.GetProperty("Availability").GetString() != "Unhealthy" ||
            data.GetProperty("Reason").GetString() != "required-quarantine" ||
            !TryNonNegative(data, "QuarantineCount", out var quarantine) || quarantine < 1 ||
            !TryNonNegative(data, "LaneCount", out var lanes) || lanes < 1 ||
            !TryNonNegative(data, "RequiredQuarantinedLaneCount", out var required) || required != 1 ||
            !TryNonNegative(data, "RequiredPressureLaneCount", out var pressure) || pressure != 0 ||
            !TryNonNegative(data, "OperationalFailureCount", out var failures) || failures != 0 ||
            !TryNonNegative(data, "RequiredProcessingQuarantineCount", out processingQuarantine) ||
            processingQuarantine < 1 || processingQuarantine > quarantine ||
            !TryNonNegative(data, "PendingCount", out _) ||
            !TryNonNegative(data, "PendingBytes", out _) ||
            !TryNonNegative(data, "LeasedCount", out _))
        {
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// The terminal processing outcome published with a lane quarantine. A retry, a missing product, or any other
    /// reason is a different failure and stays unhealthy.
    /// </summary>
    private static bool IsProcessingTerminal(JsonElement check, out long terminal, out string reason)
    {
        terminal = 0;
        reason = "capture-processing is unhealthy for a reason other than the quarantined lane's terminal outcome.";
        if (check.GetProperty("description").GetString() != ProcessingDescription ||
            !check.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            data.GetProperty("Availability").GetString() != "Unhealthy" ||
            !TryNonNegative(data, "TerminalCount", out terminal) || terminal < 1 ||
            !TryNonNegative(data, "LaneQuarantineTerminalCount", out var attributed) || attributed != terminal ||
            !TryNonNegative(data, "ProcessingQuarantineCount", out var quarantined) ||
            !TryNonNegative(data, "RetryCount", out var retry) || retry != 0 ||
            !TryNonNegative(data, "MissingProductCount", out var missing) || missing != 0 ||
            !TryNonNegative(data, "ReplayTerminalCount", out var replayTerminal) || replayTerminal != 0 ||
            !TryNonNegative(data, "ReplayRetryCount", out var replayRetry) || replayRetry != 0 ||
            !HasFalseValue(data, "DurableStateUnavailable") || !HasFalseValue(data, "ReconciliationFailed"))
        {
            return false;
        }

        var expectedReason = quarantined > 0 ? "terminal;processing-quarantine" : "terminal";
        if (data.GetProperty("Reason").GetString() != expectedReason)
        {
            return false;
        }

        reason = "";
        return true;
    }

    private static bool HasFalseValue(JsonElement data, string name)
        => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;

    private static bool TryNonNegative(JsonElement data, string name, out long value)
    {
        value = 0;
        return data.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out value) &&
               value >= 0;
    }
}
