namespace HVO.SkyMonitor.Deployment;

/// <summary>The structured result of <c>host set-product-root</c>.</summary>
internal sealed record HostPolicyResult(
    int SchemaVersion,
    string Outcome,
    string PolicyPath,
    string PreviousProductRoot,
    string ProductRoot);

/// <summary>
/// <c>host set-product-root &lt;path&gt;</c>. The operator runs it as the deployment user; the only privileged step is
/// one narrow <c>sudo</c>-executed internal command that writes the root-owned policy. That helper accepts nothing but
/// the requested root and optional mount-repair switch, re-reads and re-validates the existing policy, refuses to move a root that
/// already holds instances, and publishes the file atomically. The caller then reads the policy back as itself and
/// requires it to name exactly the requested root.
/// </summary>
internal static class PrivilegedHostPolicy
{
    internal const string CommandName = "internal-set-product-root";
    private const int SchemaVersion = 1;

    public static Task<HostPolicyResult> SetProductRootAsync(HostProductRootRequest request, CancellationToken cancellationToken)
        => SetProductRootAsync(request, new ProcessRunner(), NativeLinux.getuid(), cancellationToken);

    internal static async Task<HostPolicyResult> SetProductRootAsync(
        HostProductRootRequest request,
        IProcessRunner processRunner,
        uint uid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var location = request.PolicyLocation;
        HostPolicyChange change;
        if (!location.IsProduction || uid == 0)
        {
            // An isolated test location belongs to the invoking user, and root already holds the policy owner's
            // authority, so neither needs the privileged handoff.
            change = HostProductRootPolicy.Apply(location, request.ProductRoot, request.RepairMount);
        }
        else
        {
            var result = await processRunner.RunAsync("sudo", HelperArguments(request.ProductRoot, request.RepairMount), cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InstallerException(
                    $"Privileged host policy update failed: {Redaction.SafeDiagnostic(result.StandardError)}");
            }
            var outcome = result.StandardOutput.Trim();
            if (outcome is not (HostPolicyChange.Updated or HostPolicyChange.Unchanged))
            {
                throw new InstallerException("The privileged host policy update did not report its outcome.");
            }
            change = new HostPolicyChange(outcome, request.ProductionProductRoot, request.ProductRoot);
        }

        var resolved = HostProductRootPolicy.Resolve(location);
        if (!resolved.FromPolicy || !string.Equals(resolved.Path, request.ProductRoot, StringComparison.Ordinal))
        {
            throw new InstallerException("The host policy does not name the requested product root after the update.");
        }
        return new HostPolicyResult(SchemaVersion, change.Outcome, location.PolicyPath, change.PreviousProductRoot, resolved.Path);
    }

    internal static IReadOnlyList<string> HelperArguments(string productRoot, bool repairMount = false)
        => repairMount
            ? ["--", $"/proc/{Environment.ProcessId}/exe", CommandName, "--product-root", productRoot, "--repair"]
            : ["--", $"/proc/{Environment.ProcessId}/exe", CommandName, "--product-root", productRoot];

    /// <summary>Parses exactly <c>internal-set-product-root --product-root &lt;path&gt; [--repair]</c> with an acceptable path.</summary>
    internal static bool TryParse(string[] args, out string productRoot)
    {
        productRoot = string.Empty;
        if (args.Length is not (3 or 4) || args[0] != CommandName || args[1] != "--product-root" ||
            args.Length == 4 && args[3] != "--repair" ||
            HostProductRootPolicy.ProductRootTextError(args[2]) is not null)
        {
            return false;
        }
        productRoot = args[2];
        return true;
    }

    /// <summary>
    /// The <c>sudo</c>-executed policy write. It always writes <see cref="HostPolicyLocation.Production"/>; no argument
    /// can redirect the policy, its owner, or the trust root it is validated from.
    /// </summary>
    public static int RunInternal(string[] args)
    {
        if (!TryParse(args, out var productRoot) || NativeLinux.getuid() != 0)
        {
            return 2;
        }
        try
        {
            var change = HostProductRootPolicy.Apply(HostPolicyLocation.Production, productRoot, repairMount: args.Length == 4);
            Console.Out.WriteLine(change.Outcome);
            return 0;
        }
        catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
