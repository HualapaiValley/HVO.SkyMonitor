using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.LogicHost.Services.Elastic;

/// <summary>
/// Workload-class gate for provider adapters (#430): CameraAgent live/new-capture work is categorically ineligible
/// and is refused before any provider is called; the runner protocol refuses it again at claim.
/// </summary>
internal static class ElasticWorkloadClass
{
    public static bool IsEligible(ProcessingRunnerJobClass jobClass)
        => CentralElasticProviderOptionsEligibility.Contains(jobClass);

    public static void EnsureEligible(IEnumerable<ProcessingRunnerJobClass> jobClasses)
    {
        ArgumentNullException.ThrowIfNull(jobClasses);
        foreach (var jobClass in jobClasses)
        {
            if (!IsEligible(jobClass))
            {
                throw new ElasticWorkloadClassException(jobClass);
            }
        }
    }

    private static readonly HashSet<ProcessingRunnerJobClass> CentralElasticProviderOptionsEligibility =
        [.. Configuration.CentralElasticProviderOptions.EligibleJobClasses];
}

internal sealed class ElasticWorkloadClassException : InvalidOperationException
{
    public ElasticWorkloadClassException()
        : this(ProcessingRunnerJobClass.CameraAgentLive)
    {
    }

    public ElasticWorkloadClassException(string message)
        : base(message)
    {
    }

    public ElasticWorkloadClassException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ElasticWorkloadClassException(ProcessingRunnerJobClass jobClass)
        : base($"Job class '{jobClass}' can never be placed on an elastic provider.")
    {
        JobClass = jobClass;
    }

    public ProcessingRunnerJobClass JobClass { get; } = ProcessingRunnerJobClass.CameraAgentLive;
}

/// <summary>A request to provision one runner instance; every field is provider-neutral.</summary>
internal sealed record ElasticRunnerProvisionRequest(
    string InstanceId,
    string RunnerId,
    IReadOnlyList<ProcessingRunnerJobClass> JobClasses,
    IReadOnlyList<string> Labels,
    int MaxConcurrency,
    string? Pool,
    bool KeepWarm = false);

internal enum ElasticRunnerInstanceState
{
    Starting,
    Running,
    Stopping,
    Stopped,
    Orphaned,

    /// <summary>Its launching host stopped reconciling it; the registry denies the runner so it stops on its own.</summary>
    Abandoned
}

/// <summary>A provider's view of one instance it launched.</summary>
internal sealed record ElasticRunnerInstance(
    string InstanceId,
    string RunnerId,
    ElasticRunnerInstanceState State,
    DateTimeOffset StartedAtUtc,
    int? ProcessId);

/// <summary>What a provider can offer; recorded with every instance for provenance.</summary>
internal sealed record ElasticProviderCapabilities(
    string Provider,
    string ProcessArchitecture,
    string RuntimeImage,
    bool SupportsScaleToZero,
    IReadOnlyList<string> Labels);

/// <summary>
/// Provider-neutral provisioning/autoscaling boundary (#430). The host decides when and how many; the adapter only
/// starts, lists, and retires instances. A cloud adapter is another implementation of this interface.
/// </summary>
internal interface IElasticRunnerProvider
{
    string Name { get; }

    ElasticProviderCapabilities Capabilities { get; }

    /// <summary>Expected time from <see cref="ProvisionAsync"/> to the instance's registration.</summary>
    TimeSpan EstimateStartup();

    Task<ElasticRunnerInstance> ProvisionAsync(ElasticRunnerProvisionRequest request, CancellationToken cancellationToken);

    /// <summary>Asks the instance to drain and stop within <paramref name="grace"/>, then forces it.</summary>
    Task RetireAsync(string instanceId, TimeSpan grace, CancellationToken cancellationToken);

    /// <summary>
    /// Retires several instances: every drain request is issued first, then each instance is awaited (and forced
    /// after <paramref name="grace"/>), so no instance keeps claiming while earlier ones drain.
    /// </summary>
    async Task RetireAsync(IReadOnlyCollection<string> instanceIds, TimeSpan grace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        foreach (var instanceId in instanceIds)
        {
            await RetireAsync(instanceId, grace, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Instances the provider still knows to be alive.</summary>
    Task<IReadOnlyList<ElasticRunnerInstance>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>How a provisioned instance reads inputs and writes outputs: always job-scoped, never a central credential.</summary>
internal sealed record ElasticArtifactAccess(
    string Mode,
    Uri BaseAddress,
    IReadOnlyList<string> RequiredHeaders,
    bool DistributesCredentials);

/// <summary>
/// Provider-neutral artifact-store boundary (#430). The local implementation describes the lease-bound LogicHost
/// artifact endpoints of <c>processing-runner-v1</c>; a cloud adapter may describe job-scoped signed access instead,
/// but never one that distributes central storage credentials.
/// </summary>
internal interface IElasticArtifactAccessAdapter
{
    ElasticArtifactAccess Describe();
}

/// <summary>The local artifact access: lease-bound LogicHost endpoints, job and lease headers, no credentials.</summary>
internal sealed class LeaseScopedArtifactAccessAdapter(Uri logicHostBaseAddress) : IElasticArtifactAccessAdapter
{
    public ElasticArtifactAccess Describe()
        => new(
            "lease-scoped-http",
            logicHostBaseAddress,
            [ProcessingRunnerProtocol.RunnerIdHeader, ProcessingRunnerProtocol.JobIdHeader, ProcessingRunnerProtocol.LeaseTokenHeader],
            DistributesCredentials: false);
}
