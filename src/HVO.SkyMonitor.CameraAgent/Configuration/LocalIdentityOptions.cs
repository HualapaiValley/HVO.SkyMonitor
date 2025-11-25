using System.ComponentModel.DataAnnotations;

namespace HVO.SkyMonitor.CameraAgent.Configuration;

public sealed class LocalIdentityOptions
{
    [Required]
    [EmailAddress]
    public string AdminEmail { get; set; } = "owner@cameraagent.local";

    [Required]
    [MinLength(12)]
    public string AdminPassword { get; set; } = "ChangeMeNow!123";

    /// <summary>
    /// Optional override for the SQLite database path. Defaults to App_Data/cameraagent_identity.db.
    /// </summary>
    public string? DatabasePath { get; set; }
}
