namespace HVO.SkyMonitor.TestSupport;

/// <summary>
/// Shared test email addresses and configuration.
/// </summary>
public static class TestEmail
{
    /// <summary>
    /// Default "From" address for test emails.
    /// </summary>
    public const string FromAddress = "no-reply@skymonitor.local";

    /// <summary>
    /// Display name for the from address.
    /// </summary>
    public const string FromDisplayName = "HVO SkyMonitor (Test)";

    /// <summary>
    /// Test recipient for admin notifications.
    /// </summary>
    public const string AdminRecipient = "admin@skymonitor.local";

    /// <summary>
    /// Test recipient for operator notifications.
    /// </summary>
    public const string OperatorRecipient = "operator@skymonitor.local";

    /// <summary>
    /// Test recipient for viewer notifications.
    /// </summary>
    public const string ViewerRecipient = "viewer@skymonitor.local";

    /// <summary>
    /// Test recipient for system notifications.
    /// </summary>
    public const string SystemRecipient = "system@skymonitor.local";
}
