namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

/// <summary>A storage failure with a bounded diagnostic reason supplied by the store.</summary>
public sealed class SwpcStorageException : IOException
{
    public SwpcStorageException(string reasonCode, string message, Exception? inner)
        : base(message, inner) => ReasonCode = reasonCode;

    public SwpcStorageException()
        : this("space-weather.storage-io", "Space-weather storage failed.", null) { }

    public SwpcStorageException(string message)
        : this("space-weather.storage-io", message, null) { }

    public SwpcStorageException(string message, Exception innerException)
        : this("space-weather.storage-io", message, innerException) { }

    public string ReasonCode { get; }
}
