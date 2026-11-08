using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;

/// <summary>Registered scripts use the sole durable runner authority and its bounded off-sweep lane.</summary>
public sealed class CustomScriptTaskAdapter : ILocalAutomationPeriodicTaskAdapter
{
    private readonly Dictionary<string, CustomScriptRegistration> _scripts = new(StringComparer.Ordinal);
    private readonly bool _available;
    private int _executing;

    public CustomScriptTaskAdapter(IOptions<CustomScriptOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        if (!value.Enabled || !value.OwnerAuthorized || value.Scripts is not { Count: > 0 and <= 16 } ||
            value.Scripts.Any(static script => script is null || !script.IsValid()) ||
            value.Scripts.Select(static script => script.Id).Distinct(StringComparer.Ordinal).Count() != value.Scripts.Count)
            return;
        foreach (var script in value.Scripts.Select(static script => script.Snapshot()))
            _scripts.Add(script.Target, script);
        // An installed binary alone does not prove namespaces and limits work on this host.
        _available = LinuxCustomScriptSandbox.Probe();
    }

    public LocalAutomationTaskKind TaskKind => LocalAutomationTaskKind.CustomScript;
    public bool SupportsRunNow => true;
    public bool RunsOffSweep => true;

    public LocalAutomationTaskDescriptor Describe() => new(TaskKind,
        "Runs an owner-registered non-secret POSIX script in an isolated, bounded Linux sandbox.",
        [LocalAutomationTriggerKind.Periodic], _available ? [.. _scripts.Keys.Order(StringComparer.Ordinal)] : [],
        _available, _available ? null : "Custom scripts require owner authorization and a qualified Linux sandbox.");

    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (LocalAutomationDefinitionValidator.Validate(definition) is { } invalid) return invalid;
        return _available && definition.TaskKind == TaskKind && definition.TriggerKind == LocalAutomationTriggerKind.Periodic &&
            definition.SourceWindow is null && _scripts.ContainsKey(definition.TaskTarget)
            ? null : new("automation.script-unavailable", "definition.taskTarget");
    }

    public async ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        cancellationToken.ThrowIfCancellationRequested();
        if (!occurrence.IsValid() || Validate(occurrence.Definition) is not null)
            return new(LocalAutomationRunOutcome.Skipped, "script.unavailable");
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
            return new(LocalAutomationRunOutcome.Skipped, "script.busy");
        try
        {
            var result = await LinuxCustomScriptSandbox.ExecuteAsync(_scripts[occurrence.Definition.TaskTarget], cancellationToken)
                .ConfigureAwait(false);
            // Only fixed labels and bounded numeric metadata enter durable history or operator logs.
            return new(result.Status == "completed" && result.ExitCode == 0
                ? LocalAutomationRunOutcome.Succeeded : LocalAutomationRunOutcome.Failed,
                FormattableString.Invariant($"script.{result.Status}; exit={result.ExitCode}; stdoutBytes={result.StdoutBytes}; stderrBytes={result.StderrBytes}"));
        }
        finally { Volatile.Write(ref _executing, 0); }
    }
}
