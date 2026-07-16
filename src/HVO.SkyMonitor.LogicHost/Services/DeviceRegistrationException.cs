namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class DeviceRegistrationException : InvalidOperationException
{
    public DeviceRegistrationException()
    {
    }

    public DeviceRegistrationException(string message) : base(message)
    {
    }

    public DeviceRegistrationException(string message, string reasonCode, Guid? claimedRegistrationId = null, Guid? credentialOwnerRegistrationId = null)
        : base(message)
    {
        ReasonCode = reasonCode;
        ClaimedRegistrationId = claimedRegistrationId;
        CredentialOwnerRegistrationId = credentialOwnerRegistrationId;
    }

    public DeviceRegistrationException(string message, Exception innerException) : base(message, innerException)
    {
    }


    public string ReasonCode { get; } = "registration-invalid";
    public Guid? ClaimedRegistrationId { get; }
    public Guid? CredentialOwnerRegistrationId { get; }
}
