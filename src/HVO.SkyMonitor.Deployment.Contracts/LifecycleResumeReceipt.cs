namespace HVO.SkyMonitor.Deployment.Contracts;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515:Consider making public types internal", Justification = "Public persisted deployment contract, also source-linked into the endpoint protocol test without adding a host-to-CLI reference.")]
public sealed record LifecycleResumeReceipt(
    string State,
    long Version,
    bool Changed,
    bool Replayed,
    DateTimeOffset RequestedUtc,
    DateTimeOffset CompletedUtc);
