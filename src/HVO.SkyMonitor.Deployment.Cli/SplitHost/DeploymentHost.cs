using System.Diagnostics;
using System.Globalization;
using System.Text;
using HVO.SkyMonitor.Deployment.Contracts;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment;

/// <summary>Only OS/process primitives cross this seam. Lifecycle policy stays in the typed phase.</summary>
internal interface IDeploymentFiles : IDisposable
{
    string Call(string operation, params string[] arguments);
}
internal interface IDeploymentHost
{
    IDeploymentFiles Open(DeploymentTarget target, CancellationToken token);
    Task<ProcessResult> DockerAsync(IReadOnlyList<string> arguments, CancellationToken token);
}
internal sealed class DeploymentHost : IDeploymentHost
{
    public IDeploymentFiles Open(DeploymentTarget target, CancellationToken token) => new SshDeploymentFiles(target.SshHost, token);
    public Task<ProcessResult> DockerAsync(IReadOnlyList<string> arguments, CancellationToken token) => DockerProcessAsync(arguments, token);
    private static async Task<ProcessResult> DockerProcessAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        // env removes every endpoint/TLS override before Docker resolves the explicit pinned endpoint.
        return await new ProcessRunner().RunAsync("env", ["-u", "DOCKER_CONTEXT", "-u", "DOCKER_HOST", "-u", "DOCKER_TLS_VERIFY", "-u", "DOCKER_CERT_PATH", "docker", .. arguments], token).ConfigureAwait(false);
    }
}

internal sealed class SshDeploymentFiles : IDeploymentFiles
{
    private readonly Process process;
    private readonly CancellationToken token;
    private readonly CancellationTokenSource deadline;
    private readonly Task<string> errors;
    public SshDeploymentFiles(string host, CancellationToken cancellationToken, bool local = false)
    {
        token = cancellationToken;
        deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromHours(1));
        process = new Process { StartInfo = new ProcessStartInfo { FileName = local ? "bash" : "ssh", RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        if (local) { process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add(Adapter); }
        else
        {
            foreach (var arg in new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=3", "--", host, "bash -c " + Quote(Adapter) }) process.StartInfo.ArgumentList.Add(arg);
        }
        process.StartInfo.Environment.Remove("HVO_GITHUB_TOKEN");
        Require(process.Start(), "ssh-unavailable");
        errors = process.StandardError.ReadToEndAsync(deadline.Token);
    }
    public string Call(string operation, params string[] arguments)
    {
        token.ThrowIfCancellationRequested();
        using var command = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); command.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var line = operation + " " + string.Join(' ', arguments.Select(value => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) is { Length: > 0 } encoded ? encoded : "-"));
            Require(line.Length <= 128 * 1024, "ssh-request-too-large");
            process.StandardInput.WriteLine(line); process.StandardInput.Flush();
            var response = process.StandardOutput.ReadLineAsync(command.Token).AsTask().GetAwaiter().GetResult();
            Require(response is { Length: <= 2 * 1024 * 1024 } && response.StartsWith("ok ", StringComparison.Ordinal), "host-primitive-failed");
            return Encoding.UTF8.GetString(Convert.FromBase64String(response![3..]));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw Error("host-deadline-exceeded"); }
    }
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    public void Dispose()
    {
        try { process.StandardInput.Close(); if (!process.WaitForExit(1000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
        finally { deadline.Cancel(); process.Dispose(); deadline.Dispose(); }
        _ = errors.Exception;
    }
    // An authenticated SSH session holds file/directory descriptors and flock for its lifetime. The adapter has no
    // prepare, recovery, inventory, ownership, journal, or deletion policy. It exposes bounded Linux primitives only.
    internal const string Adapter = """
        set -eu
        export LC_ALL=C
        while IFS=' ' read -r op rest; do
          args=(); for encoded in $rest; do
            if [[ "$encoded" == - ]]; then args+=(''); else args+=("$(printf '%s' "$encoded" | base64 -d)"); fi
          done
          a=${args[0]:-}; b=${args[1]:-}
          case "$op" in
            probe) value="$(cat /etc/machine-id 2>/dev/null || hostname) $(hostname) $(id -u) $(id -u "$a") $(uname -m)" ;;
            stat) if [[ -e "$a" || -L "$a" ]]; then value=$(stat -c '%u %a %f %h %d %i' -- "$a"); else value=absent; fi ;;
            fstat) value=$(stat -Lc '%u %a %f %h %d %i' -- "/proc/$$/fd/$a") ;;
            open) exec {fd}<"$a"; value=$fd ;;
            lock) flock -n "$a"; value=locked ;;
            read) value=$(head -c 1048577 -- "/proc/$$/fd/$a" | base64 -w0) ;;
            list) value=$(find -P "$a" -mindepth 1 -maxdepth 1 -printf '%f\0' | head -c 1048577 | base64 -w0) ;;
            mounts) value=$(head -c 1048577 /proc/self/mountinfo | base64 -w0) ;;
            path) value="/proc/$$/fd/$a" ;;
            move) mv -T -n -- "$a" "$b"; value=moved ;;
            unlink) rm -f -- "$a"; value=removed ;;
            rmdir) rmdir -- "$a"; value=removed ;;
            sync) sync -f -- "$a"; value=synced ;;
            *) exit 96 ;;
          esac
          printf 'ok %s\n' "$(printf '%s' "$value" | base64 -w0)"
        done
        """;
}
internal sealed record DeploymentNode(uint Owner, int Mode, int Type, int Links, string Identity)
{
    public bool Directory => Type == 0x4000;
    public bool File => Type == 0x8000;
    public static DeploymentNode? Parse(string value)
    {
        if (value == "absent") return null;
        var parts = value.Split(' '); Require(parts.Length == 6, "invalid-host-response");
        return new(uint.Parse(parts[0], CultureInfo.InvariantCulture), Convert.ToInt32(parts[1], 8), Convert.ToInt32(parts[2], 16) & 0xf000,
            (Convert.ToInt32(parts[2], 16) & 0xf000) == 0x4000 ? 0 : int.Parse(parts[3], CultureInfo.InvariantCulture), parts[4] + ":" + parts[5]);
    }
}
