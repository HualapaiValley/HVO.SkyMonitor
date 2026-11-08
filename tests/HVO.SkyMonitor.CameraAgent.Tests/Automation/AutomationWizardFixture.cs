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

internal sealed class AutomationWizardFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvo-wizard", Guid.NewGuid().ToString("N"));

    internal AutomationWizardFixture(string zone = "America/Phoenix", double latitude = 35.347, double longitude = -113.878)
    {
        Calendar = new(ObservingDayCalendar.ForDeployment(LocalAutomationWindowTests.Site(zone, latitude, longitude)));
        Planner = new(Calendar);
        Store = NewStore();
        Service = NewService();
    }

    internal MutableTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
    internal WizardRegistry Registry { get; } = new();
    internal FixedObservingDayCalendarProvider Calendar { get; }
    internal LocalAutomationWindowPlanner Planner { get; }
    internal SqliteLocalAutomationStore Store { get; private set; }
    internal CameraAgentAutomationUiService Service { get; private set; }
    internal bool Authorized { get; set; } = true;

    internal static LocalAutomationSaveRequest Request(string id = "daily",
        LocalAutomationSourceWindowKind kind = LocalAutomationSourceWindowKind.SunriseDay)
        => new(id, id, true, LocalAutomationTaskKind.StillImageGeneration, "keogram",
            LocalAutomationTriggerKind.SourceWindowClosed, 1, 0, Guid.NewGuid().ToString("N"), "untrusted", "wizard",
            LocalAutomationWindowTests.Policy(kind) with { ProcessingSettleAllowance = TimeSpan.FromMinutes(15) });

    internal async Task RestartAsync()
    {
        Store.Dispose();
        Store = NewStore();
        Service = NewService();
        await Store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private SqliteLocalAutomationStore NewStore() => new(Registry, new StubCaptureSequenceSource(),
        Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root }),
        Clock, NullLogger<SqliteLocalAutomationStore>.Instance, windowPlanner: Planner);

    private CameraAgentAutomationUiService NewService()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "wizard-owner"),
            new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
        ], IdentityConstants.ApplicationScheme));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(principal, null, It.IsAny<string>()))
            .Returns(() => Task.FromResult(Authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed()));
        return new(new WizardAuthentication(principal), authorization.Object, Store,
            NullLogger<CameraAgentAutomationUiService>.Instance, Registry, Planner, Clock);
    }

    public void Dispose()
    {
        Store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class WizardAuthentication(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }

    internal sealed class WizardRegistry : ILocalAutomationTaskRegistry
    {
        internal bool Available { get; set; } = true;
        internal bool Throw { get; set; }
        internal List<LocalAutomationOccurrence> Executed { get; } = [];

        public IReadOnlyList<LocalAutomationTaskDescriptor> Describe()
            => Throw ? throw new IOException("Synthetic private path") :
            [new(LocalAutomationTaskKind.StillImageGeneration, "Synthetic registered still producer",
                [LocalAutomationTriggerKind.SourceWindowClosed], ["keogram", "star-trail"], Available,
                Available ? null : "Producer unavailable.")
                { SupportedSourceWindows = [LocalAutomationSourceWindowKind.CompletedCivilHour, LocalAutomationSourceWindowKind.SunriseDay] }];

        public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
        {
            if (!Available || definition.TaskKind != LocalAutomationTaskKind.StillImageGeneration ||
                !Describe()[0].Targets.Contains(definition.TaskTarget, StringComparer.Ordinal))
                return new(LocalAutomationContract.UnregisteredTargetReasonCode, "definition.taskTarget");
            var selection = definition.TaskTarget == "star-trail"
                ? LocalAutomationSourceSelection.DarkNightActualSources : LocalAutomationSourceSelection.AllActualSources;
            return definition.SourceWindow?.Selection == selection ? null
                : new(LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.sourceWindow.selection");
        }

        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationDefinition definition, string runKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
        {
            Executed.Add(occurrence);
            return ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Succeeded, "Synthetic product."));
        }
    }
}
