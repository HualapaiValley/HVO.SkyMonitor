using Microsoft.Extensions.Configuration;

namespace HVO.SkyMonitor.Common.Configuration;

public static class DeploymentKeyPerFile
{
    public const string DirectoryVariable = "HVO_KEY_PER_FILE_DIRECTORY";

    public static void AddConfiguredDirectory(ConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
        {
            throw new InvalidOperationException("The deployment KeyPerFile directory must be an existing absolute path.");
        }
        configuration.AddKeyPerFile(directory, optional: false);
    }
}
