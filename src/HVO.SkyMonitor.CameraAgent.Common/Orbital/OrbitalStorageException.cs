namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>
/// An orbital store operation could not complete durably. <see cref="ReasonCode"/> is a stable, bounded code
/// (for example <c>orbital.storage-no-space</c>); the last-known-good snapshot remains active.
/// </summary>
public sealed class OrbitalStorageException : IOException
{
    public const string DefaultReasonCode = "orbital.storage-io";

    public OrbitalStorageException(string reasonCode, string message, Exception? inner)
        : base(message, inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ReasonCode = reasonCode;
    }

    public OrbitalStorageException()
        : this(DefaultReasonCode, "Orbital storage failed.", null)
    {
    }

    public OrbitalStorageException(string message)
        : this(DefaultReasonCode, message, null)
    {
    }

    public OrbitalStorageException(string message, Exception innerException)
        : this(DefaultReasonCode, message, innerException)
    {
    }

    public string ReasonCode { get; }
}
