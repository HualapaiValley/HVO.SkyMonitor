namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class DeviceRegistrationException : InvalidOperationException
{
    public DeviceRegistrationException()
    {
    }

    public DeviceRegistrationException(string message) : base(message)
    {
    }

    public DeviceRegistrationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
