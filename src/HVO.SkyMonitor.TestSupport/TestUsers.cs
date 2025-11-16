namespace HVO.SkyMonitor.TestSupport;

/// <summary>
/// Shared test user identities with roles and credentials.
/// </summary>
public static class TestUsers
{
    /// <summary>
    /// Administrator user with full system access.
    /// </summary>
    public static class Admin
    {
        public const string Email = "admin@skymonitor.local";
        public const string Username = "admin";
        public const string Password = "Admin123!@#";
        public const string FullName = "Test Administrator";
        public static readonly string[] Roles = ["Administrator", "Operator", "Viewer"];
    }

    /// <summary>
    /// Operator user with operational capabilities.
    /// </summary>
    public static class Operator
    {
        public const string Email = "operator@skymonitor.local";
        public const string Username = "operator";
        public const string Password = "Operator123!@#";
        public const string FullName = "Test Operator";
        public static readonly string[] Roles = ["Operator", "Viewer"];
    }

    /// <summary>
    /// Viewer user with read-only access.
    /// </summary>
    public static class Viewer
    {
        public const string Email = "viewer@skymonitor.local";
        public const string Username = "viewer";
        public const string Password = "Viewer123!@#";
        public const string FullName = "Test Viewer";
        public static readonly string[] Roles = ["Viewer"];
    }

    /// <summary>
    /// Regular user with no special roles.
    /// </summary>
    public static class Regular
    {
        public const string Email = "user@skymonitor.local";
        public const string Username = "user";
        public const string Password = "User123!@#";
        public const string FullName = "Test User";
        public static readonly string[] Roles = [];
    }
}
