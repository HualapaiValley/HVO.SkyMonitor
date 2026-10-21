using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// Fixed operator text for the space-weather reason codes, mirroring <see cref="OrbitalOperatorText"/>. A code without
/// text, including any <c>automation.*</c> code, is shown as generic text with the code itself.
/// </summary>
internal static class SpaceWeatherOperatorText
{
    internal const string GenericText = "The command was not completed.";

    private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal)
    {
        // Status and commands.
        ["space-weather.disabled"] =
            "Space weather is turned off in this CameraAgent's settings. Retained notices stay readable.",
        ["space-weather.definition-missing"] =
            "No refresh schedule exists. Create the space-weather-refresh automation to schedule refreshes.",
        ["space-weather.definition-disabled"] =
            "The refresh schedule is disabled. Enable the space-weather-refresh automation to refresh.",
        ["space-weather.halted"] = "SWPC fetching is halted. Resume the halt before refreshing.",
        ["space-weather.refresh-queued"] =
            "Refresh queued. The automation runner checks every enabled product on its next poll; the provider's own cadence decides what is fetched.",
        ["space-weather.refresh-replayed"] =
            "Already queued. This refresh request was recorded before, so nothing new was queued.",
        ["space-weather.invalid-idempotency-key"] = "The request identifier was not valid. Try again.",
        ["space-weather.invalid-command"] = "The command was rejected as invalid.",
        ["space-weather.invalid-paging"] = "The notice page request was not valid.",
        ["space-weather.busy"] = "The space-weather store is busy with another change. Try again.",
        [SpaceWeatherAvailability.StoreUnavailableReasonCode] = "The space-weather store is unavailable.",
        ["space-weather.antiforgery-invalid"] = "The request could not be verified. Reload the page and try again.",

        // Resume and acknowledge.
        ["resumed"] = "Resumed. The next run fetches again within the provider's own cadence.",
        ["not-halted"] = "Nothing to resume: that halt is no longer active.",
        ["acknowledged"] = "Acknowledged. The notice stays listed until it ends.",
        ["replayed"] = "Already applied. This request was recorded before, so nothing changed again.",
        ["space-weather.resume-halt-changed"] =
            "The halt changed after this page read it. Review the current halt before resuming.",
        ["space-weather.resume-invalid-halt-time"] = "The halt time must be the UTC time shown for the current halt.",
        ["space-weather.resume-invalid-scope"] = "The halt to resume is not valid for this CameraAgent.",
        ["space-weather.revision-conflict"] =
            "The space-weather store changed while the command ran. Try again; the same request is safe to repeat.",
        ["space-weather.idempotency-key-reused"] =
            "This request identifier was already used with different content. Try again as a new request.",
        ["space-weather.idempotency-conflict"] =
            "This request identifier was already used with different content. Try again as a new request.",
        ["space-weather.unknown-notice"] = "That notice is no longer retained. Reload to see the current notices.",
        ["space-weather.group-not-found"] = "That notice is no longer retained. Reload to see the current notices.",

        // Health.
        ["space-weather.ready"] = "Space-weather data is being fetched normally.",
        ["space-weather.provider-halted"] = "SWPC fetching is halted for every product until an operator resumes it.",
        ["space-weather.product-halted"] = "SWPC fetching is halted for a product until an operator resumes it.",
        ["space-weather.alerts-outdated"] =
            "The alerts product has not been fetched successfully for too long, so the notices shown may be out of date.",
        ["space-weather.format-drift"] =
            "A product's responses have been rejected repeatedly; the provider's format may have changed.",
        ["space-weather.record-drift"] =
            "Some alert records could not be read; the provider's record format may have changed.",
        ["space-weather.first-success-overdue"] = "A product's first successful fetch is overdue.",
        ["space-weather.store-oversized"] = "The space-weather store holds more retained data than expected.",

        // Run outcomes.
        ["space-weather.succeeded"] = "The run completed.",
        ["space-weather.nothing-due"] = "Skipped: no enabled product was due yet.",
        ["space-weather.product-failed"] = "At least one product fetch did not succeed.",
        ["space-weather.storage-failed"] = "The run could not be stored durably.",
        ["space-weather.retention-failed"] = "Retention of expired notices could not run.",
        ["space-weather.attempt-in-progress"] = "Another fetch was already running, so this occurrence was skipped.",
        ["space-weather.attempt-not-found"] = "The fetch attempt was no longer recorded.",
        ["space-weather.snapshot-conflict"] = "The fetched snapshot conflicted with the stored one and was not recorded.",
        ["space-weather.recovery-incomplete"] = "Startup recovery of interrupted fetches did not complete.",
        ["space-weather.journal-paging"] = "The fetch journal could not be read consistently.",

        // Storage.
        ["space-weather.storage-io"] = "A disk error affected the space-weather store.",
        ["space-weather.storage-locked"] = "Another process holds the space-weather store.",

        // Schedule validation.
        ["space-weather.reserved-definition-id"] =
            "The space-weather refresh task accepts only the identifier space-weather-refresh.",
        ["space-weather.interval-out-of-range"] = string.Create(CultureInfo.InvariantCulture,
            $"The interval must be between {SpaceWeatherAutomationAdapter.MinimumIntervalSeconds} and {SpaceWeatherAutomationAdapter.MaximumIntervalSeconds} seconds."),
    };

    private static readonly Dictionary<string, string> HaltTexts = new(StringComparer.Ordinal)
    {
        ["rate-limited"] = "the provider rate-limited repeated requests",
        ["forbidden"] = "the provider refused access",
        ["endpoint-moved"] = "the provider endpoint moved",
        ["not-found"] = "the provider did not find the product",
        ["gone"] = "the provider no longer serves the product",
        ["client-error"] = "the provider rejected the request",
    };

    /// <summary>Whether <paramref name="reasonCode"/> has fixed operator text rather than the generic fallback.</summary>
    internal static bool IsKnown(string reasonCode) => Texts.ContainsKey(reasonCode);

    /// <summary>Operator text for <paramref name="reasonCode"/>; any other code is generic text naming the code.</summary>
    internal static string Describe(string? reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            return "No reason was recorded.";
        }
        return Texts.TryGetValue(reasonCode, out var text) ? text : $"{GenericText} ({reasonCode})";
    }

    /// <summary>The cause of a fetch halt; an unrecognized halt reason is shown as its code.</summary>
    internal static string DescribeHalt(string reasonCode)
        => HaltTexts.TryGetValue(reasonCode, out var text) ? $"{text} ({reasonCode})" : reasonCode;
}
