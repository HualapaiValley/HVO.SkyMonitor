namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

public sealed class InstallationVerificationPendingException : InvalidOperationException
{
    public InstallationVerificationPendingException() : this(requiresScrub: true) { }
    public InstallationVerificationPendingException(string message) : base(message) { }
    public InstallationVerificationPendingException(string message, Exception innerException) : base(message, innerException) { }
    public InstallationVerificationPendingException(bool requiresScrub)
        : base("Installation verification requires a completed current integrity checkpoint.") => RequiresScrub = requiresScrub;
    internal InstallationVerificationPendingException(bool requiresScrub, bool configurationChanged) : this(requiresScrub)
        => ConfigurationChanged = configurationChanged;
    internal bool ConfigurationChanged { get; }
    public bool RequiresScrub { get; } = true;
}
