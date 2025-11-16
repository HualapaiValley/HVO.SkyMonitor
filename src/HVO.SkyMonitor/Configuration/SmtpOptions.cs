namespace HVO.SkyMonitor.Configuration;

/// <summary>
/// Configuration for SMTP email delivery.
/// </summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 25;

    public bool UseSsl { get; set; }

    public string From { get; set; } = "no-reply@skymonitor.local";

    public string FromDisplayName { get; set; } = "SkyMonitor";

    public string? Username { get; set; }

    public string? Password { get; set; }
}
