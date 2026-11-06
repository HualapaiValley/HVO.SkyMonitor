using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// The compact orbital data notice: requested-population coverage, element age and the stale and overdue states, or
/// the explicit "no orbital data installed" wording. Freshness comes from <see cref="OrbitalDataStatusView"/>, which
/// applies the health evaluator's own pure rules, so there is one freshness rule for every page that embeds this notice
/// and for health. The stale and overdue lines are independent facts: both can show, and they show even while refresh
/// is halted, quarantined or needs repair, where health reports only its first degrading reason.
/// </summary>
public sealed partial class OrbitalDataStatusNotice : ComponentBase
{
    internal const string StatusUnavailableText =
        "The orbital data status could not be read, so nothing is known about the installed orbital data. Satellite predictions are unavailable; this does not mean no satellites are overhead.";
    internal const string StoreUnavailableText =
        "The orbital store is unavailable, so the installed orbital data cannot be described. Satellite predictions are unavailable; this does not mean no satellites are overhead.";
    internal const string ElementAgeUnknownText =
        "The element age is unknown because the snapshot manifest cannot be read, so the elements are treated as stale. They stay in use until a refresh replaces them.";
    private NoticeContent _content = Compose(null);

    /// <summary>The composed status, or null when it could not be read.</summary>
    [Parameter] public OrbitalDataStatusView? View { get; set; }

    [Parameter] public string Id { get; set; } = "orbital-data-notice";

    protected override void OnParametersSet() => _content = Compose(View);

    /// <summary>
    /// The notice content for a view. An unreadable status or store makes no data claim at all; no active snapshot is
    /// stated as missing data, never as an empty sky; coverage is always relative to the requested population.
    /// </summary>
    internal static NoticeContent Compose(OrbitalDataStatusView? view)
    {
        if (view is null)
        {
            return new NoticeContent("Orbital data status unavailable.", [StatusUnavailableText], "failure");
        }
        if (view.StoreUnavailable)
        {
            return new NoticeContent("Orbital data unknown.", [StoreUnavailableText], "failure");
        }
        var repair = view.Status.RecoveryAction == OrbitalRefreshRecoveryAction.RepairStorage
            ? string.Concat("Orbital storage needs repair: ", OrbitalOperatorText.ReasonText(view.Status.Reason),
                " ", OrbitalOperatorText.Recovery(view.Status.RecoveryAction))
            : null;
        if (!view.HasData)
        {
            var noDataLines = new List<string> { OrbitalOperatorText.NoDataText };
            if (HaltLine(view) is { } noDataHalt)
            {
                noDataLines.Add(noDataHalt);
            }
            if (repair is not null)
            {
                noDataLines.Add(repair);
            }
            return new NoticeContent(null, noDataLines, noDataLines.Count > 1 ? "warning" : "pending");
        }
        var lines = new List<string>(4);
        if (view.Manifest is not { } manifest)
        {
            lines.Add(string.Concat("Orbital elements are installed, but their coverage cannot be shown. ",
                OrbitalOperatorText.Describe(view.ManifestUnavailableReason)));
        }
        else
        {
            var coverage = manifest.Coverage;
            lines.Add(string.Concat("Requested population: ", coverage.RequestedPopulation, ". ",
                OrbitalOperatorText.Completeness(coverage.Completeness), "."));
            lines.Add(coverage.KnownExclusions.Count == 0
                ? "No known exclusions are listed."
                : string.Concat("Known exclusions: ", string.Join(", ", coverage.KnownExclusions), "."));
            if (view.NewestElementAge is { } newest)
            {
                lines.Add(string.Concat("Newest element epoch is ", HealthPage.FormatDuration(newest), " old."));
            }
        }
        if (view.ElementsStale)
        {
            lines.Add(view.ElementAgeUnknown
                ? ElementAgeUnknownText
                : string.Create(CultureInfo.InvariantCulture,
                    $"The elements are stale: even the newest epoch is older than {OrbitalElementsHealthEvaluator.ElementStaleAge.TotalHours:0} h, so predictions from them are less accurate. They stay in use until a refresh replaces them."));
        }
        if (view.RefreshOverdue)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"Refresh is overdue: no successful retrieval in the last {view.RefreshOptions.StaleAfterHours} h. The installed elements stay in use."));
        }
        var halt = HaltLine(view);
        if (halt is not null)
        {
            lines.Add(halt);
        }
        if (repair is not null)
        {
            lines.Add(repair);
        }
        var headline = view.ElementsStale
            ? "Orbital elements are stale."
            : view.RefreshOverdue
                ? "Orbital refresh is overdue."
                : repair is not null
                    ? "Orbital storage needs repair."
                    : "Orbital elements installed.";
        var tone = view.ElementsStale || view.RefreshOverdue || view.Manifest is null || halt is not null || repair is not null
            ? "warning" : "success";
        return new NoticeContent(headline, lines, tone);
    }

    /// <summary>
    /// The provider halt, if any. Its status renders through <see cref="OrbitalOperatorText.HaltStatus"/>, so a halt
    /// with no valid HTTP status (<c>orbital.provider-outcome-unknown</c> or <c>orbital.invalid-http-status</c>) says so
    /// explicitly instead of showing a number.
    /// </summary>
    private static string? HaltLine(OrbitalDataStatusView view)
        => view.Status.ProviderHalt is { } halt
            ? string.Concat("Provider halted: ", OrbitalOperatorText.HaltStatus(halt.HttpStatusCode),
                ". Refresh waits until an operator resumes the provider.")
            : null;

    internal sealed record NoticeContent(string? Headline, IReadOnlyList<string> Lines, string Tone);
}
