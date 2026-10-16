using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.CameraAgent.Data;

namespace HVO.SkyMonitor.CameraAgent.Configuration;

public sealed class LocalIdentityOptions : IValidatableObject
{
    public const string DefaultCookieName = "CameraAgent.Auth";

    /// <summary>The sign-in name a newly seeded owner receives unless another is configured.</summary>
    public const string DefaultAdminUserName = "admin";

    /// <summary>The non-routable owner email used when none is configured.</summary>
    public const string DefaultAdminEmail = "owner@cameraagent.local";

    /// <summary>
    /// The durable owner key. Ownership, authorization, bootstrap state, and recovery all match the owner by this
    /// email; it is also accepted as a sign-in name.
    /// </summary>
    [Required]
    [EmailAddress]
    public string AdminEmail { get; set; } = DefaultAdminEmail;

    /// <summary>
    /// The user name given to a newly seeded owner. An owner that already exists keeps the user name it was created
    /// with, so an upgraded installation still signs in with its email. It cannot contain <c>@</c>, because a
    /// sign-in name containing <c>@</c> is resolved as an email.
    /// </summary>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    [RegularExpression("^[A-Za-z0-9._-]+$")]
    public string AdminUserName { get; set; } = DefaultAdminUserName;

    public string AdminPassword { get; set; } = string.Empty;

    public bool AllowMissingAdminPassword { get; set; }

    [Required]
    [StringLength(128, MinimumLength = 1)]
    [RegularExpression("^[A-Za-z0-9._-]+$")]
    public string CookieName { get; set; } = DefaultCookieName;

    /// <summary>
    /// Optional override for the SQLite database path. Defaults to App_Data/cameraagent_identity.db.
    /// </summary>
    public string? DatabasePath { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrEmpty(AdminPassword))
        {
            if (!AllowMissingAdminPassword)
            {
                yield return new ValidationResult(
                    "AdminPassword is required until local identity has been seeded.",
                    [nameof(AdminPassword)]);
            }
            yield break;
        }

        if (AdminPassword.Length < CameraAgentIdentityPolicy.MinimumPasswordLength)
        {
            yield return new ValidationResult(
                $"AdminPassword must be at least {CameraAgentIdentityPolicy.MinimumPasswordLength} characters.",
                [nameof(AdminPassword)]);
        }
    }
}
