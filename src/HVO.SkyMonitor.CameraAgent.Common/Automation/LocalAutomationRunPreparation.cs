using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>Retained owner command evidence for an explicit preparation, separate from the product occurrence.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LocalAutomationRunPreparation(
    [property: JsonRequired] string Operation,
    [property: JsonRequired] string Actor,
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] string CommandSha256,
    [property: JsonRequired] DateTimeOffset RequestedAtUtc,
    [property: JsonRequired] string IdentitySha256)
{
    public bool IsValid() => Operation is "backfill" or "retry" &&
        LocalAutomationDefinitionValidator.IsText(Actor, 256) &&
        LocalAutomationDefinitionValidator.IsText(IdempotencyKey, 128) &&
        LocalAutomationDefinitionValidator.IsText(Reason, 512) &&
        CommandSha256 is { Length: 64 } && CommandSha256.All(char.IsAsciiHexDigit) &&
        RequestedAtUtc.Offset == TimeSpan.Zero &&
        string.Equals(IdentitySha256, ComputeIdentity(this), StringComparison.OrdinalIgnoreCase);

    internal static LocalAutomationRunPreparation Create(string operation, string actor, string key,
        string reason, string commandSha256, DateTimeOffset requestedAtUtc)
    {
        var value = new LocalAutomationRunPreparation(operation, actor, key, reason, commandSha256, requestedAtUtc, string.Empty);
        return value with { IdentitySha256 = ComputeIdentity(value) };
    }

    private static string ComputeIdentity(LocalAutomationRunPreparation value) =>
        CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Contract = "hvo-local-automation-run-preparation-v1",
            value.Operation,
            value.Actor,
            value.IdempotencyKey,
            value.Reason,
            value.CommandSha256,
            value.RequestedAtUtc
        });
}
