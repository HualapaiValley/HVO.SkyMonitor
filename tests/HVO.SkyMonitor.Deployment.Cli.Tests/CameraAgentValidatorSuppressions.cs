using System.Diagnostics.CodeAnalysis;

// Source-linked product types retain their host visibility; the password policy supplies the real length constant.
[assembly: SuppressMessage("Maintainability", "CA1515:Consider making public types internal", Scope = "type", Target = "~T:HVO.SkyMonitor.CameraAgent.Configuration.LocalIdentityOptions", Justification = "Unmodified source-linked product validator.")]
[assembly: SuppressMessage("Maintainability", "CA1515:Consider making public types internal", Scope = "type", Target = "~T:HVO.SkyMonitor.CameraAgent.Data.ApplicationUser", Justification = "Unmodified source-linked product identity dependency.")]
[assembly: SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Scope = "type", Target = "~T:HVO.SkyMonitor.CameraAgent.Data.CameraAgentPasswordValidator", Justification = "The source-linked identity policy uses this type; these tests exercise startup options validation.")]
