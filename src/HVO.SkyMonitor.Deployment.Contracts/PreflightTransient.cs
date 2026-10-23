using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Deployment.Contracts;

/// <summary>Existing transient-confirm read-only predicates; these do not authorize or perform a review.</summary>
public static class PreflightTransient
{
    private static readonly string[] HashFields = ["requestSha256", "recipientSha256", "messageSnippetSha256"];
    public static bool Allowlist(JsonObject document, string run) => Safe(() =>
        Keys(document, "events runId schemaVersion") && Number(document, "schemaVersion") == 1 && Text(document, "runId") == run &&
        document["events"] is JsonArray { Count: >= 1 and <= 32 } events &&
        events.All(n => n is JsonObject item && Keys(item, "agentId centralTransientEventId") && GuidText(Text(item, "centralTransientEventId")) && Text(item, "agentId").Length is >= 1 and <= 128) &&
        events.Select(n => Lower(Text(n!, "centralTransientEventId"))).Distinct(StringComparer.Ordinal).Count() == events.Count);

    public static bool DetailEligible(JsonObject document, string id, string agent, string marker) => Safe(() =>
    {
        var summary = document["summary"]!; var reviews = document["event"]!["reviews"] as JsonArray;
        var last = reviews?.LastOrDefault();
        return Lower(Text(summary, "centralTransientEventId")) == id && Text(summary, "agentId") == agent &&
            (Number(summary, "reviewState") == 0 || Number(summary, "reviewState") == 1 && last is not null && Text(last, "assessmentId") == Text(summary, "activeAssessmentId") && Number(last, "disposition") == 1 && HasMarker(last, marker)) &&
            Number(summary, "effectiveClassification") == 1 && Number(summary, "effectiveMeteorSeverity") is 0 or 1 &&
            Match(Text(summary, "activeAssessmentId"), "[0-9a-fA-F-]{36}") && Match(Text(summary, "eTag"), "\"[A-Za-z0-9_-]+\"");
    });
    public static bool AuditReady(JsonObject document, string review, string assessment, string marker) => Safe(() =>
    {
        var selected = ((JsonArray)document["event"]!["reviews"]!).Where(n => Text(n!, "reviewId") == review && Text(n!, "assessmentId") == assessment && Number(n!, "disposition") == 1 && Text(n!, "reviewerIdentity") == "redacted" && HasMarker(n!, marker)).ToArray();
        return Number(document["summary"]!, "reviewState") == 1 && selected.Length == 1 && ((JsonArray)document["event"]!["notifications"]!).Any(n => Text(n!, "assessmentId") == assessment && Number(n!, "state") == 1 && string.CompareOrdinal(Text(n!, "createdUtc"), Text(selected[0]!, "createdUtc")) >= 0);
    });
    public static string? MailpitId(JsonObject document, IReadOnlyCollection<string> baseline, string id, string recipient, string started, bool resumed)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(baseline);
        try
        {
            var selected = ((JsonArray)document["messages"]!).Where(n => (resumed || !baseline.Contains(Text(n!, "iD"), StringComparer.Ordinal)) && Text(n!, "subject") == "SkyMonitor meteor review" &&
                string.CompareOrdinal(Text(n!, "created"), started) >= 0 && ((JsonArray)n!["to"]!).Any(to => Text(to!, "address") == recipient) && Text(n, "snippet").Contains(id, StringComparison.Ordinal)).ToArray();
            return selected.Length == 1 ? Text(selected[0]!, "iD") : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException or InvalidCastException) { return null; }
    }
    public static bool Evidence(JsonObject document, string run, string inventory, string revision, string allowlistHash, JsonObject allowlist) => Safe(() =>
    {
        if (!Keys(document, "allowlistSha256 completedAt events inventorySha256 mode phaseStatus runId schemaVersion sourceRevision startedAt") || Number(document, "schemaVersion") != 1 || Text(document, "runId") != run || Text(document, "mode") != "isolated" || Text(document, "inventorySha256") != inventory || Text(document, "sourceRevision") != revision || Text(document, "allowlistSha256") != allowlistHash || Text(document, "phaseStatus") != "passed" || document["startedAt"] is null || document["completedAt"] is null) return false;
        _ = Text(document, "startedAt"); _ = Text(document, "completedAt");
        var events = (JsonArray)document["events"]!; var expected = (JsonArray)allowlist["events"]!;
        return events.Count == expected.Count && events.All(n => n is JsonObject item && Keys(item, "agentId centralTransientEventId mailpitMessageId messageSnippetSha256 notificationId recipientSha256 requestSha256 reviewId") &&
            Match(Text(item, "centralTransientEventId"), "[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}") && Match(Text(item, "reviewId"), "[0-9a-fA-F-]{36}") && Match(Text(item, "notificationId"), "[0-9a-fA-F-]{36}") && Text(item, "mailpitMessageId").Length is >= 1 and <= 256 &&
            HashFields.All(key => Match(Text(item, key), "[0-9a-f]{64}"))) &&
            events.Select(n => (Text(n!, "centralTransientEventId"), Text(n!, "agentId"))).Order().SequenceEqual(expected.Select(n => (Lower(Text(n!, "centralTransientEventId")), Text(n!, "agentId"))).Order());
    });
    public static bool OidcDiscovery(string body, string status, string authority) => Safe(() =>
    {
        if (status != "200" || body.Length > 65536 || JsonNode.Parse(body) is not JsonObject document) return false;
        return Text(document, "issuer") == authority.TrimEnd('/') + "/" && Text(document, "token_endpoint") == authority.TrimEnd('/') + "/connect/token";
    });
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308", Justification = "The retained evidence protocol canonicalizes UUIDs to lowercase.")]
    private static string Lower(string value) => value.ToLowerInvariant();
    private static bool HasMarker(JsonNode node, string marker) => ((JsonArray)node["reasonCodes"]!).Any(n => n!.GetValue<string>() == marker);
    private static bool Keys(JsonObject document, string keys) => document.Select(p => p.Key).Order(StringComparer.Ordinal).SequenceEqual(keys.Split(' ').Order(StringComparer.Ordinal));
    private static int Number(JsonNode node, string key) => node[key]!.GetValue<int>();
    private static string Text(JsonNode node, string key) => node[key]!.GetValue<string>();
    private static bool GuidText(string value) => Match(value, "[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
    private static bool Match(string value, string pattern) => Regex.IsMatch(value, "\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static bool Safe(Func<bool> predicate)
    {
        try { return predicate(); }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException or InvalidCastException or FormatException or System.Text.Json.JsonException or ArgumentException) { return false; }
    }
}
