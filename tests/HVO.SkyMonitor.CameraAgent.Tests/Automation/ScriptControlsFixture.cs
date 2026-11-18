using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

internal sealed class ScriptControlsFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvo-script-controls", Guid.NewGuid().ToString("N"));
    private readonly IOptions<CameraAgentHostOptions> _options;
    internal ScriptControlsFixture(ILocalAutomationTaskRegistry? registry = null, MutableTimeProvider? clock = null)
    {
        Clock = clock ?? new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
        Registry = registry ?? Stub;
        _options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root });
        Store = NewStore();
        Commands = new(Store, Worker);
        Runner = new(Store, Registry, new StubCaptureSequenceSource(), _options, Clock,
            NullLogger<LocalAutomationRunnerService>.Instance, offSweepWorker: Worker);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "script-owner"),
            new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
        ], IdentityConstants.ApplicationScheme));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(principal, null, It.IsAny<string>()))
            .Returns(() => Task.FromResult(Authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed()));
        Service = new(new Authentication(principal), authorization.Object, Store,
            NullLogger<CameraAgentAutomationUiService>.Instance, Registry,
            new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(
                ObservingDayCalendar.ForDeployment(LocalAutomationWindowTests.Site()))), Clock, Commands);
    }

    internal string DatabasePath => Path.Combine(_root, ".automation", "local-automations.db");
    internal MutableTimeProvider Clock { get; }
    internal ScriptRegistry Stub { get; } = new();
    internal ILocalAutomationTaskRegistry Registry { get; }
    internal LocalAutomationOffSweepWorker Worker { get; } = new();
    internal SqliteLocalAutomationStore Store { get; }
    internal LocalAutomationRunnerService Runner { get; }
    internal LocalAutomationScriptCommands Commands { get; }
    internal CameraAgentAutomationUiService Service { get; }
    internal bool Authorized { get; set; } = true;
    internal SqliteLocalAutomationStore NewStore() => new(Registry, new StubCaptureSequenceSource(),
        _options, Clock, NullLogger<SqliteLocalAutomationStore>.Instance);
    internal static LocalAutomationSaveRequest Save(string target = "registered.digest", string id = "script")
        => new(id, "Operator check", true, LocalAutomationTaskKind.CustomScript, target,
            LocalAutomationTriggerKind.Periodic, 60, 0, "save-" + id, "untrusted", null);
    internal static LocalAutomationRunNowRequest RunNow(string key = "run") => new("script", 1, key, "untrusted", "check");
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal async Task AddNewerBuiltInHistoryAsync()
    {
        await Store.SaveAsync(Save("built-in", "builtin") with
        { TaskKind = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition }, default).ConfigureAwait(false);
        var builtin = (await Store.GetRunnerViewAsync(default).ConfigureAwait(false)).Single(item => item.Definition.DefinitionId == "builtin");
        for (var index = 0; index <= LocalAutomationContract.MaximumProjectedRuns; index++)
            await Store.RecordTerminalRunAsync(builtin, "newer-built-in-" + index, Clock.GetUtcNow(),
                LocalAutomationRunOutcome.Succeeded, "done", null, default).ConfigureAwait(false);
    }
    public void Dispose()
    {
        Runner.Dispose();
        Worker.Dispose();
        Store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private sealed class Authentication(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
    internal sealed class ScriptRegistry : ILocalAutomationTaskRegistry
    {
        internal bool Available { get; set; } = true;
        internal string UnavailableReason { get; set; } = "script.disabled";
        internal string Target { get; set; } = "registered.digest";
        internal Func<LocalAutomationOccurrence, CancellationToken, ValueTask<LocalAutomationExecution>> Execute { get; set; }
            = (_, _) => ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Succeeded, "script.completed; exit=0; stdoutBytes=0; stderrBytes=0"));
        public IReadOnlyList<LocalAutomationTaskDescriptor> Describe() => [
            new(LocalAutomationTaskKind.CustomScript, "Registered script", [LocalAutomationTriggerKind.Periodic],
                Available ? [Target] : [], Available, Available ? null : UnavailableReason)
            { ScriptInvocations = Available ? [new(Target, "registered", 30, 1048576, 65536, 1)] : [] },
            new(LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition, "Built-in", [LocalAutomationTriggerKind.Periodic], ["built-in"], true, null)];
        public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
            => Describe().Any(item => item.Available && item.TaskKind == definition.TaskKind && item.Targets.Contains(definition.TaskTarget))
                ? null : new(LocalAutomationContract.UnregisteredTargetReasonCode, "definition.taskTarget");
        public bool RunsOffSweep(LocalAutomationDefinition definition) => definition.TaskKind == LocalAutomationTaskKind.CustomScript;
        public bool SupportsRunNow(LocalAutomationDefinition definition) => true;
        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationDefinition definition, string runKey, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
            => Execute(occurrence, cancellationToken);
    }
}
