using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

/// <summary>
/// One product version (install contract section 1, #1310): the VersionPrefix in Directory.Build.props. A build given no
/// version is its development build, X.Y.Z-dev; Directory.Build.targets refuses a build given any version but the prefix
/// or a pre-release of it, and an informational version other than the version, so a release cannot ship a program
/// that says another version. Each case evaluates a throwaway project against the repository's own two files.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed partial class ProductVersionBuildCheckTests
{
    [TestMethod]
    public void DirectoryBuildProps_DeclaresOneReleaseVersionAsTheProductVersion()
    {
        var prefixes = Regex.Matches(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Build.props")),
            "<VersionPrefix>([^<]*)</VersionPrefix>",
            RegexOptions.CultureInvariant);

        Assert.HasCount(1, prefixes);
        StringAssert.Matches(prefixes[0].Groups[1].Value, VersionPattern());
    }

    [TestMethod]
    public async Task Build_GivenNoVersion_IsTheProductVersionsDevelopmentBuild()
    {
        var (exitCode, output) = await MSBuildAsync("-getProperty:Version");

        // One property is printed as its bare value.
        Assert.AreEqual(0, exitCode, output);
        Assert.AreEqual($"{Prefix()}-dev", output.Trim());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("-rc.1")]
    [DataRow("-dev.3")]
    public async Task Build_GivenTheProductVersionOrAPreReleaseOfIt_IsAccepted(string preRelease)
    {
        var (exitCode, output) = await MSBuildAsync("-t:HvoCheckProductVersion", $"-p:Version={Prefix()}{preRelease}");

        Assert.AreEqual(0, exitCode, output);
    }

    [TestMethod]
    [DataRow("-p:Version=99.0.0", "Version '99.0.0' is not the product version")]
    [DataRow("-p:Version=99.0.0-rc.1", "Version '99.0.0-rc.1' is not the product version")]
    [DataRow("-p:Version={prefix}-", "Version '{prefix}-' is not the product version")]
    [DataRow("-p:Version={prefix}.1", "Version '{prefix}.1' is not the product version")]
    [DataRow("-p:InformationalVersion=99.0.0", "The informational version '99.0.0' is not the version '{prefix}-dev'")]
    [DataRow("-p:InformationalVersion={prefix}-DEV", "The informational version '{prefix}-DEV' is not the version '{prefix}-dev'")]
    public async Task Build_GivenAnotherVersion_IsRefused(string argument, string message)
    {
        ArgumentNullException.ThrowIfNull(argument);
        ArgumentNullException.ThrowIfNull(message);
        var (exitCode, output) = await MSBuildAsync("-t:HvoCheckProductVersion", argument.Replace("{prefix}", Prefix(), StringComparison.Ordinal));

        Assert.AreNotEqual(0, exitCode, output);
        StringAssert.Contains(output, "HVO1310", StringComparison.Ordinal);
        StringAssert.Contains(output, message.Replace("{prefix}", Prefix(), StringComparison.Ordinal), StringComparison.Ordinal);
    }

    private static string Prefix()
        => Regex.Match(
                File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Build.props")),
                "<VersionPrefix>([^<]*)</VersionPrefix>",
                RegexOptions.CultureInvariant)
            .Groups[1].Value;

    /// <summary>
    /// Runs MSBuild on a project with no sources of its own, outside the repository, with the repository's
    /// Directory.Build.props and Directory.Build.targets, and returns its exit code with everything it printed.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> MSBuildAsync(params string[] arguments)
    {
        var root = Directory.CreateTempSubdirectory("hvo-product-version-");
        try
        {
            var project = Path.Combine(root.FullName, "probe.csproj");
            await File.WriteAllTextAsync(
                project, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>""");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root.FullName
            };
            foreach (var argument in (string[])["msbuild", project, "-nologo", "-nodeReuse:false",
                         $"-p:DirectoryBuildPropsPath={Path.Combine(RepositoryRoot(), "Directory.Build.props")}",
                         $"-p:DirectoryBuildTargetsPath={Path.Combine(RepositoryRoot(), "Directory.Build.targets")}", .. arguments])
            {
                start.ArgumentList.Add(argument);
            }
            // A VERSION environment variable would set the version as a property: this probe sets none.
            start.Environment.Remove("VERSION");
            start.Environment.Remove("Version");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet msbuild did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await output + await error);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
