using System.Net;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Cli.Client;

/// <summary>
/// CameraAgent answered, and refused or failed the request: its status and what its problem details said. The message
/// is CameraAgent's title, or a sentence about the status; nothing here holds a value the request sent.
/// </summary>
internal sealed class SkyApiException : Exception
{
    public SkyApiException()
    {
    }

    public SkyApiException(string message)
        : base(message)
    {
    }

    public SkyApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SkyApiException(HttpStatusCode statusCode, string message, string? code = null, string? detail = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Detail = detail;
    }

    public HttpStatusCode StatusCode { get; init; }

    /// <summary>CameraAgent's problem code, such as <c>settings.conflict</c>, when it gave one.</summary>
    public string? Code { get; init; }

    public string? Detail { get; init; }

    /// <summary>Validation messages by setting key (<c>settings.invalid</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Errors { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>Why each setting cannot be changed (<c>settings.readOnly</c>).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The problem document itself, for <c>--json</c>; null when the body was not one.</summary>
    public JsonElement? Problem { get; init; }
}

/// <summary>CameraAgent's answer could not be read: it was not the JSON this client expects.</summary>
internal sealed class SkyProtocolException : Exception
{
    public SkyProtocolException()
    {
    }

    public SkyProtocolException(string message)
        : base(message)
    {
    }

    public SkyProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
