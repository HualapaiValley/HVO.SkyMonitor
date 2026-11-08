using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;

/// <summary>Registered scripts use the sole durable runner authority and its bounded off-sweep lane.</summary>
public sealed class CustomScriptTaskAdapter : ILocalAutomationPeriodicTaskAdapter
{
    private readonly Dictionary<string, CustomScriptRegistration> _scripts = new(StringComparer.Ordinal);
    private readonly bool _available;
    private readonly TimeProvider _timeProvider;
    private string? _unavailableReason;
    private static readonly Action<ILogger, string, Exception?> Unavailable = LoggerMessage.Define<string>(
        LogLevel.Information, new EventId(7410, "ScriptUnavailable"), "Custom script availability: {ReasonCode}");
    private int _executing;

    public CustomScriptTaskAdapter(IOptions<CustomScriptOptions> options, TimeProvider? timeProvider = null,
        ILogger<CustomScriptTaskAdapter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        CustomScriptOptions value;
        try { value = options.Value; }
        catch (Exception exception) when (exception is InvalidOperationException or OptionsValidationException)
        {
            Reject("script.binding-invalid");
            return;
        }
        if (!value.Enabled) { Reject("script.disabled"); return; }
        if (!value.OwnerAuthorized) { Reject("script.owner-authorization-required"); return; }
        if (value.Scripts is not { Count: > 0 and <= 16 } ||
            value.Scripts.Any(static script => script is null || !script.IsValid()))
        { Reject("script.registration-invalid"); return; }
        if (value.Scripts.Select(static script => script.Id).Distinct(StringComparer.Ordinal).Count() != value.Scripts.Count)
        { Reject("script.registration-duplicate"); return; }
        foreach (var script in value.Scripts.Select(static script => script.Snapshot())) _scripts.Add(script.Target, script);
        _unavailableReason = LinuxCustomScriptSandbox.ProbeUnavailableReason();
        _available = _unavailableReason is null;
        if (!_available && logger is not null) Unavailable(logger, _unavailableReason!, null);

        void Reject(string reason)
        {
            _unavailableReason = reason;
            if (logger is not null) Unavailable(logger, reason, null);
        }
    }

    public LocalAutomationTaskKind TaskKind => LocalAutomationTaskKind.CustomScript;
    public bool SupportsRunNow => true;
    public bool RunsOffSweep => true;

    public LocalAutomationTaskDescriptor Describe() => new(TaskKind,
        "Runs an owner-registered non-secret POSIX script in an isolated, bounded Linux sandbox.",
        [LocalAutomationTriggerKind.Periodic], _available ? [.. _scripts.Keys.Order(StringComparer.Ordinal)] : [],
        _available, _unavailableReason);

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
            var execution = new LocalAutomationExecution(result.Status == "cancelled" ? LocalAutomationRunOutcome.Interrupted :
                result.Status == "completed" && result.ExitCode == 0 ? LocalAutomationRunOutcome.Succeeded : LocalAutomationRunOutcome.Failed,
                FormattableString.Invariant($"script.{result.Status}; exit={result.ExitCode}; stdoutBytes={result.StdoutBytes}; stderrBytes={result.StderrBytes}"));
            if (result.Status == "cancelled")
                throw new LocalAutomationDrainedCancellationException(execution, _timeProvider.GetUtcNow(), cancellationToken);
            return execution;
        }
        finally { Volatile.Write(ref _executing, 0); }
    }
}
