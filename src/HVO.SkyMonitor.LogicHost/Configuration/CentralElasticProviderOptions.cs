using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.LogicHost.Configuration;

/// <summary>Which elastic runner provider adapter the host uses; <see cref="None"/> disables provisioning.</summary>
internal enum CentralElasticProviderKind
{
    None,

    /// <summary>The bounded local proof adapter: self-hosted runner processes launched on this host on demand.</summary>
    LocalProcess
}

/// <summary>Settings of the <see cref="CentralElasticProviderKind.LocalProcess"/> adapter.</summary>
internal sealed class CentralLocalProcessElasticOptions
{
    /// <summary>Path of the runner executable (the published <c>HVO.SkyMonitor.ProcessingRunner</c> binary or a launcher script).</summary>
    public string? Executable { get; init; }

    /// <summary>Optional arguments passed before the runner's own (for example a <c>dotnet</c> host followed by the runner dll).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    /// <summary>LogicHost base address the launched runner registers against.</summary>
    public string? LogicHostUrl { get; init; }

    public string ClientId { get; init; } = "system-processing-runner";

    /// <summary>File holding the runner client secret; the secret is never placed in the host configuration.</summary>
    public string? ClientSecretFile { get; init; }

    /// <summary>
    /// Optional runner self-termination after idleness, a safety net for a host that disappears. Zero (default) means
    /// instances are stopped only by the host; a configured value is raised to outlive the host's scale-to-zero window
    /// (see <see cref="CentralElasticProviderOptions.EffectiveInstanceIdleShutdown"/>).
    /// </summary>
    public TimeSpan IdleShutdown { get; init; }

    public bool AllowInsecureHttp { get; init; }
}

/// <summary>
/// Optional elastic runner provisioning for <c>processing-runner-v1</c> (#430). Absent and disabled by default: the
/// self-hosted reference deployment, builds, tests, and every local workflow are complete without it. When enabled,
/// the autoscaler provisions runner instances through the configured provider adapter for provider-eligible,
/// runner-placed backlog within the bounds below; work is retained locally whenever provider startup cannot meet
/// the queue deadline or a limit is reached.
/// </summary>
internal sealed class CentralElasticProviderOptions
{
    public const string SectionName = "ElasticProviders";

    /// <summary>Job classes a provider adapter may ever receive; <see cref="ProcessingRunnerJobClass.CameraAgentLive"/> is categorically excluded.</summary>
    public static readonly IReadOnlyList<ProcessingRunnerJobClass> EligibleJobClasses =
        [ProcessingRunnerJobClass.CentralRecipe, ProcessingRunnerJobClass.CameraAgentArchivedReplay];

    public bool Enabled { get; init; }

    public CentralElasticProviderKind Provider { get; init; } = CentralElasticProviderKind.None;

    /// <summary>Upper bound on concurrently provisioned instances.</summary>
    public int MaxInstances { get; init; } = 2;

    /// <summary>Instances kept warm even without backlog; 0 scales to zero.</summary>
    public int MinWarmInstances { get; init; }

    /// <summary>Idle time after which an instance above the warm minimum is retired.</summary>
    public TimeSpan ScaleToZeroAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Expected time from provisioning to the instance's registration; measured cold starts refine it.</summary>
    public TimeSpan ExpectedColdStart { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Placement policy: backlog is only worth a cold start when it can still be served within this deadline.</summary>
    public TimeSpan QueueDeadline { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Global cost/resource limit on provisioned instance minutes per UTC day; 0 means unlimited.</summary>
    public int MaxInstanceMinutesPerDay { get; init; }

    /// <summary>Concurrency each provisioned instance advertises.</summary>
    public int MaxConcurrencyPerInstance { get; init; } = 1;

    /// <summary>Optional runner pool the instances join (see the entitlement pools); reserved so shared work is served after the pool's.</summary>
    public string? Pool { get; init; }

    /// <summary>Additional capability labels every instance advertises.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan RetireGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>An instance that has not registered within this time after provisioning is treated as failed and retired.</summary>
    public TimeSpan RegistrationTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public CentralLocalProcessElasticOptions LocalProcess { get; init; } = new();

    /// <summary>Label namespaces the host controls; configuration may not set them.</summary>
    public static readonly string[] ReservedLabelPrefixes = ["provider:", "elastic-instance:", "pool:", "pool-mode:"];

    /// <summary>
    /// The idle shutdown handed to each instance, coordinated with the scaling policy: warm-minimum instances never
    /// self-terminate, and any other instance outlives the host's scale-to-zero decision window so it is retired by
    /// the host (and accounted) rather than exiting on its own and being recorded as an orphan. The runner's own idle
    /// exit remains the safety net for a host that disappears.
    /// </summary>
    public TimeSpan EffectiveInstanceIdleShutdown()
    {
        if (MinWarmInstances > 0 || LocalProcess.IdleShutdown <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        var minimum = ScaleToZeroAfter + (SampleInterval * 2) + RetireGrace;
        return LocalProcess.IdleShutdown > minimum ? LocalProcess.IdleShutdown : minimum;
    }

    public bool Validate(out string? error)
    {
        error = null;
        if (!Enabled)
        {
            return true;
        }
        if (Provider == CentralElasticProviderKind.None)
        {
            error = "ElasticProviders:Provider must name an adapter when ElasticProviders is enabled.";
            return false;
        }
        if (MaxInstances is < 1 or > 256 || MinWarmInstances < 0 || MinWarmInstances > MaxInstances
            || MaxConcurrencyPerInstance < 1 || MaxConcurrencyPerInstance > ProcessingRunnerProtocol.MaximumConcurrency
            || MaxInstanceMinutesPerDay < 0)
        {
            error = "ElasticProviders instance bounds are invalid.";
            return false;
        }
        if (ScaleToZeroAfter <= TimeSpan.Zero || ExpectedColdStart <= TimeSpan.Zero || QueueDeadline <= TimeSpan.Zero
            || SampleInterval <= TimeSpan.Zero || RetireGrace <= TimeSpan.Zero || RegistrationTimeout <= TimeSpan.Zero
            || ScaleToZeroAfter > TimeSpan.FromDays(1) || QueueDeadline > TimeSpan.FromDays(1) || ExpectedColdStart > TimeSpan.FromHours(1))
        {
            error = "ElasticProviders timing values are invalid.";
            return false;
        }
        if (Pool is not null && (!CentralProcessingEntitlementOptions.IsValidPool(Pool) || !ProcessingRunnerProtocol.IsValidLabel($"pool:{Pool}")))
        {
            error = "ElasticProviders:Pool must be a pool name (lower-case letters, digits, '-', not 'shared') whose 'pool:' label fits the label limit.";
            return false;
        }
        if (Labels.Count > ProcessingRunnerProtocol.MaximumLabelCount - 4 || Labels.Any(label => !ProcessingRunnerProtocol.IsValidLabel(label))
            || Labels.Any(label => ReservedLabelPrefixes.Any(prefix => label.StartsWith(prefix, StringComparison.Ordinal))))
        {
            error = "ElasticProviders:Labels must be lower-case labels and may not set provider, instance, or pool control labels.";
            return false;
        }
        if (Provider == CentralElasticProviderKind.LocalProcess)
        {
            if (string.IsNullOrWhiteSpace(LocalProcess.Executable) || string.IsNullOrWhiteSpace(LocalProcess.LogicHostUrl)
                || !Uri.TryCreate(LocalProcess.LogicHostUrl, UriKind.Absolute, out _)
                || string.IsNullOrWhiteSpace(LocalProcess.ClientId) || string.IsNullOrWhiteSpace(LocalProcess.ClientSecretFile)
                || LocalProcess.IdleShutdown < TimeSpan.Zero)
            {
                error = "ElasticProviders:LocalProcess requires Executable, an absolute LogicHostUrl, ClientId, and ClientSecretFile.";
                return false;
            }
        }
        return true;
    }
}
