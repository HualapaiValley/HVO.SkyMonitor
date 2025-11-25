using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.CameraAgent.Data;

/// <summary>
/// Camera agent identity user. Simplified compared to LogicHost but keeps
/// room for future expansion (MFA, passkeys, etc.).
/// </summary>
public sealed class ApplicationUser : IdentityUser
{
    /// <summary>
    /// Indicates whether the account represents a physical site owner.
    /// </summary>
    public bool IsSiteOwner { get; set; }
}
