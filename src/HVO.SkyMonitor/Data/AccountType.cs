namespace HVO.SkyMonitor.Data;

/// <summary>
/// Defines the type of account for authorization and authentication purposes.
/// </summary>
public enum AccountType
{
    /// <summary>
    /// Human user account with interactive login capabilities.
    /// Can sign in via password, passkeys, or external providers.
    /// </summary>
    User = 0,

    /// <summary>
    /// System/service account for non-interactive authentication.
    /// Can only authenticate via API keys or client credentials flow.
    /// Cannot sign in interactively or use passwords.
    /// </summary>
    System = 1
}
