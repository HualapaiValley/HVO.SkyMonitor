using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.Data;

/// <summary>
/// Application user with API key support and account type distinction.
/// </summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>
    /// Type of account (User or System).
    /// </summary>
    public AccountType AccountType { get; set; }

    /// <summary>
    /// UTC timestamp when the account was created.
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// UTC timestamp of the last successful login.
    /// </summary>
    public DateTime? LastLoginUtc { get; set; }

    /// <summary>
    /// Indicates whether the account is active and can authenticate.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// API keys associated with this user.
    /// </summary>
    public ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();
}
