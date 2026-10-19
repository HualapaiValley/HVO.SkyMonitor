namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The documented first sign-in of a newly installed CameraAgent owner. CameraAgent seeds a new owner with the user
/// name <see cref="UserName"/>, and the installer supplies <see cref="Password"/> as its temporary password unless
/// the operator chooses one with <c>--password-file</c> or asks for a random one with <c>--generate-password</c>.
/// CameraAgent requires the password to be replaced at first sign-in and refuses the default as a replacement.
/// </summary>
internal static class OwnerCredentialDefaults
{
    /// <summary>The non-routable owner email used when <c>--owner-email</c> is not supplied.</summary>
    internal const string Email = "owner@cameraagent.local";

    internal const string UserName = "admin";

    internal const string Password = "skymonitor";

    /// <summary>The shortest operator-supplied temporary password the installer and owner recovery accept.</summary>
    internal const int MinimumPasswordLength = 8;

    /// <summary>The longest operator-supplied temporary password the installer and owner recovery accept.</summary>
    internal const int MaximumPasswordLength = 256;
}
