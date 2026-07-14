using System.ComponentModel.DataAnnotations;

namespace HVO.SkyMonitor.CameraAgent.Configuration;

public sealed class LocalIdentityOptions : IValidatableObject
{
    [Required]
    [EmailAddress]
    public string AdminEmail { get; set; } = "owner@cameraagent.local";

    [Required]
    [MinLength(12)]
    public string AdminPassword { get; set; } = string.Empty;

    /// <summary>
    /// Optional override for the SQLite database path. Defaults to App_Data/cameraagent_identity.db.
    /// </summary>
    public string? DatabasePath { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrEmpty(AdminPassword))
        {
            yield break;
        }

        if (!AdminPassword.Any(char.IsUpper))
        {
            yield return new ValidationResult(
                "AdminPassword must contain an uppercase character.",
                [nameof(AdminPassword)]);
        }
        if (!AdminPassword.Any(char.IsLower))
        {
            yield return new ValidationResult(
                "AdminPassword must contain a lowercase character.",
                [nameof(AdminPassword)]);
        }
        if (!AdminPassword.Any(char.IsDigit))
        {
            yield return new ValidationResult(
                "AdminPassword must contain a digit.",
                [nameof(AdminPassword)]);
        }
        if (!AdminPassword.Any(static character => !char.IsLetterOrDigit(character)))
        {
            yield return new ValidationResult(
                "AdminPassword must contain a non-alphanumeric character.",
                [nameof(AdminPassword)]);
        }
    }
}
