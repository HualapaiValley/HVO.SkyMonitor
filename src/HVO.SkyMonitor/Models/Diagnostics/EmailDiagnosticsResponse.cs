namespace HVO.SkyMonitor.Models.Diagnostics;

/// <summary>
/// Response summarizing an SMTP diagnostics send attempt.
/// </summary>
public sealed class EmailDiagnosticsResponse
{
    public string Recipient { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public bool Sent { get; set; }
}
