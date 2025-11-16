namespace HVO.SkyMonitor.Models.Diagnostics;

/// <summary>
/// Request to send an SMTP test message.
/// </summary>
public sealed class EmailDiagnosticsRequest
{
    public string Recipient { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
}
