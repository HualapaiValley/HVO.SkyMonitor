using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlanCarry_SqliteAbortReasonContainingKeyIsTerminalSafe(bool json)
    {
        using var fixture = await MigratingFixtureAsync();
        var target = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, AutomationStore);
        ExecuteSql(target, FixtureDump(AutomationStore));
        const string key = "agent\u001b[31m\"";
        SaveDefinition(target, key, 1);
        ExecuteSql(target, "UPDATE automation_definition_revisions SET definition_json='{}';" +
            "CREATE TRIGGER refuse_update BEFORE UPDATE ON automation_definitions BEGIN SELECT RAISE(ABORT, NEW.definition_id); END;");
        StateCounterFloor[] floors = [new(AutomationStore, "automation-definition-version", key, 2)];
        var plan = CameraAgentStateCounters.PlanCarry(floors, _ => target, Path.Combine(fixture.Root, "reason-trial"));
        Assert.HasCount(1, plan.Uncarried);
        StringAssert.Contains(plan.Uncarried[0].Reason, "SQLite Error 19", StringComparison.Ordinal);
        StringAssert.Contains(plan.Uncarried[0].Reason, key, StringComparison.Ordinal);
        using var error = new StringWriter();
        await CameraAgentStateSnapshot.ShowRestoreUncarriedAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), DateTimeOffset.UtcNow), plan.Uncarried, json, error);
        var warning = error.ToString();
        if (json)
        {
            using var document = JsonDocument.Parse(warning);
            warning = document.RootElement.GetProperty("warning").GetString()!;
        }
        Console.WriteLine(JsonSerializer.Serialize(warning));
        // The reason SQLite gave, without the key's decoration, which redaction removes first (#1491).
        StringAssert.Contains(warning,
            JsonEncodedText.Encode(plan.Uncarried[0].Reason.Replace("\u001b[31m", string.Empty, StringComparison.Ordinal)).ToString(),
            StringComparison.Ordinal);
        Assert.IsFalse(warning.Contains(key, StringComparison.Ordinal), "The reason echoed the raw counter key.");
        Assert.IsFalse(warning.Contains('\u001b', StringComparison.Ordinal), "The warning contains a terminal escape character.");
        using var redactedError = new StringWriter();
        await CameraAgentStateSnapshot.ShowRestoreUncarriedAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), DateTimeOffset.UtcNow),
            plan.Uncarried.Select(static entry => (entry.What, entry.Reason + " token=\"carry-redaction-sentinel\"")).ToArray(),
            json, redactedError);
        var redactedWarning = redactedError.ToString();
        if (json)
        {
            using var document = JsonDocument.Parse(redactedWarning);
            redactedWarning = document.RootElement.GetProperty("warning").GetString()!;
        }
        Assert.IsFalse(redactedWarning.Contains("carry-redaction-sentinel", StringComparison.Ordinal),
            "Escaping a key must retain diagnostic redaction.");
        StringAssert.Contains(redactedWarning, JsonEncodedText.Encode("<redacted>").ToString(), StringComparison.Ordinal);
    }
}
