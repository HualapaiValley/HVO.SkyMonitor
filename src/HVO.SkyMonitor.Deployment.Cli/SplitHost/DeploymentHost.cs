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
    IDeploymentFiles OpenPreflight(DeploymentTarget target, CancellationToken token) => Open(target, token);
    Task<ProcessResult> DockerAsync(IReadOnlyList<string> arguments, CancellationToken token);
}
internal sealed class DeploymentHost : IDeploymentHost
{
    public IDeploymentFiles Open(DeploymentTarget target, CancellationToken token) => new SshDeploymentFiles(target.SshHost, token);
    public IDeploymentFiles OpenPreflight(DeploymentTarget target, CancellationToken token) => new SshDeploymentFiles(target.SshHost, token, readOnly: true);
    public Task<ProcessResult> DockerAsync(IReadOnlyList<string> arguments, CancellationToken token) => DockerProcessAsync(arguments, token);
    private static async Task<ProcessResult> DockerProcessAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        // env removes every endpoint/TLS override before Docker resolves the explicit pinned endpoint.
        return await new ProcessRunner(4 * 1024 * 1024).RunAsync("env", ["-u", "DOCKER_CONTEXT", "-u", "DOCKER_HOST", "-u", "DOCKER_TLS_VERIFY", "-u", "DOCKER_CERT_PATH", "docker", .. arguments], token).ConfigureAwait(false);
    }
}

internal sealed class SshDeploymentFiles : IDeploymentFiles
{
    private readonly Process process;
    private readonly CancellationToken token;
    private readonly CancellationTokenSource deadline;
    private readonly Task errors;
    public SshDeploymentFiles(string host, CancellationToken cancellationToken, bool local = false, bool readOnly = false)
    {
        token = cancellationToken;
        deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromHours(1));
        process = new Process { StartInfo = new ProcessStartInfo { FileName = local ? "bash" : "ssh", RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        var adapter = readOnly ? ReadOnlyAdapter : Adapter;
        if (local) { process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add(adapter); }
        else
        {
            foreach (var arg in new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=3", "--", host, (readOnly ? "" : "timeout --signal=TERM --kill-after=30s 3600 ") + "bash -c " + Quote(adapter) }) process.StartInfo.ArgumentList.Add(arg);
        }
        process.StartInfo.Environment.Remove("HVO_GITHUB_TOKEN");
        Require(process.Start(), "ssh-unavailable");
        errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
    }
    public string Call(string operation, params string[] arguments)
    {
        token.ThrowIfCancellationRequested();
        using var command = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); command.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var line = operation + " " + string.Join(' ', arguments.Select(value => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) is { Length: > 0 } encoded ? encoded : "-"));
            Require(line.Length <= 128 * 1024, "ssh-request-too-large");
            process.StandardInput.WriteLineAsync(line.AsMemory(), command.Token).GetAwaiter().GetResult();
            process.StandardInput.FlushAsync(command.Token).GetAwaiter().GetResult();
            var response = ReadReplyAsync(process.StandardOutput, command.Token).GetAwaiter().GetResult();
            Require(response is { Length: <= 2 * 1024 * 1024 } && response.StartsWith("ok ", StringComparison.Ordinal), "host-primitive-failed");
            return Encoding.UTF8.GetString(Convert.FromBase64String(response![3..]));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw Error("host-deadline-exceeded"); }
        catch (InstallerException) when (process.WaitForExit(100) && process.ExitCode == 127) { throw Error("tool-unavailable"); }
        catch (IOException) when (process.WaitForExit(100) && process.ExitCode == 127) { throw Error("tool-unavailable"); }
    }
    private static async Task<string> ReadReplyAsync(StreamReader reader, CancellationToken token)
    {
        var reply = new StringBuilder(); var character = new char[1];
        while (reply.Length <= 2 * 1024 * 1024)
        {
            var count = await reader.ReadAsync(character.AsMemory(), token).ConfigureAwait(false);
            Require(count != 0, "host-disconnected");
            if (character[0] == '\n') return reply.ToString();
            reply.Append(character[0]);
        }
        throw Error("host-response-too-large");
    }
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    public void Dispose()
    {
        try
        {
            // A remote tool can exit before consuming the request. A broken pipe while closing stdin must
            // not replace its bounded exit-127 diagnostic (or the original cancellation/transport failure).
            try { process.StandardInput.Close(); } catch (IOException) { }
            if (!process.WaitForExit(1000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        }
        finally { deadline.Cancel(); process.Dispose(); deadline.Dispose(); }
        _ = errors.Exception;
    }
    // An authenticated SSH session holds file/directory descriptors and flock for its lifetime. The adapter has no
    // prepare, recovery, inventory, ownership, journal, or deletion policy. It exposes bounded Linux primitives only.
    internal const string Adapter = """
        set -euo pipefail
        export LC_ALL=C
        umask 077
        while IFS=' ' read -r op rest; do
          args=(); for encoded in $rest; do
            if [[ "$encoded" == - ]]; then args+=(''); else decoded="$(printf '%s' "$encoded" | base64 -d; printf '.')"; args+=("${decoded%.}"); fi
          done
          a=${args[0]:-}; b=${args[1]:-}
          case "$op" in
            facts)
              identity=$(cat /etc/machine-id 2>/dev/null || hostname)
              cpu=$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf unknown)
              memory=$(awk '/^MemTotal:/ {print $2}' /proc/meminfo 2>/dev/null || printf unknown)
              thermal=unsupported; throttle=unsupported
              if [[ -r /sys/class/thermal/thermal_zone0/temp ]]; then
                temperature=$(cat /sys/class/thermal/thermal_zone0/temp 2>/dev/null || true)
                [[ ! "$temperature" =~ ^-?[0-9]+$ ]] || thermal=reported:$temperature
              fi
              if command -v vcgencmd >/dev/null 2>&1; then
                throttled=$(vcgencmd get_throttled 2>/dev/null || true)
                [[ ! "$throttled" =~ ^throttled=0x[0-9a-fA-F]+$ ]] || throttle=reported:${throttled#throttled=}
              fi
              value=$(printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s' "$identity" "$(hostname)" "$(id -u)" "$(uname -m)" "$cpu" "${memory:-unknown}" "$(date +%s)" "$thermal" "$throttle") ;;
            uid) value=$(id -u) ;;
            space) value=$(df -Pk -- "$a" 2>/dev/null | awk 'NR==2 {print $4}') ;;
            sockets)
              if ! command -v ss >/dev/null 2>&1 || ! sockets=$(ss -H -ltn "sport = :$a" 2>/dev/null); then value=unsupported
              elif [[ -n "$sockets" ]]; then value=present; else value=none; fi ;;
            tcp)
              if ! command -v bash >/dev/null || ! command -v timeout >/dev/null; then value=tool-unavailable
              elif timeout 8 bash -c 'exec 3<>"/dev/tcp/$1/$2"' _ "$a" "$b" 2>/dev/null; then value=reachable; else value=unreachable; fi ;;
            http-reachable)
              if ! command -v bash >/dev/null || ! command -v curl >/dev/null; then value=tool-unavailable
              elif curl --silent --show-error --max-time 8 --output /dev/null "$a" 2>/dev/null; then value=reachable; else value=unreachable; fi ;;
            probe) value="$(cat /etc/machine-id 2>/dev/null || hostname) $(hostname) $(id -u) $(id -u "$a") $(uname -m)" ;;
            stat) if [[ -e "$a" || -L "$a" ]]; then value=$(stat -c '%u %a %f %h %d %i' -- "$a"); else value=absent; fi ;;
            fstat) value=$(stat -Lc '%u %a %f %h %d %i' -- "/proc/$$/fd/$a") ;;
            open) exec {fd}<"$a"; value=$fd ;;
            create) set -C; exec {fd}>"$a"; set +C; value=$fd ;;
            append-bytes) printf '%s' "$b" | base64 -d >&"$a"; value=written ;;
            lock) flock -n "$a"; value=locked ;;
            read) value=$(head -c 1048577 -- "/proc/$$/fd/$a" | base64 -w0) ;;
            list) value=$(find -P "$a/." -mindepth 1 -maxdepth 1 -printf '%f\0' | head -c 1048577 | base64 -w0) ;;
            mounts) value=$(head -c 1048577 /proc/self/mountinfo | base64 -w0) ;;
            path) value="/proc/$$/fd/$a" ;;
            move) mv -T -n -- "$a" "$b"; value=moved ;;
            replace) mv -Tf -- "$a" "$b"; value=moved ;;
            unlink) rm -f -- "$a"; value=removed ;;
            rmdir) rmdir -- "$a"; value=removed ;;
            mkdir) mkdir -m 700 -- "$a"; value=created ;;
            chmod700) chmod 700 -- "$a"; value=secured ;;
            write) (umask 077; set -C; printf '%s' "$b" > "$a"); value=written ;;
            http) value=$(curl "${args[@]}"); value="$value" ;;
            sync) sync -- "$a"; value=synced ;;
            *) exit 96 ;;
          esac
          printf 'ok %s\n' "$(printf '%s' "$value" | base64 -w0)"
        done
        """;
    // Preflight cannot invoke a mutating primitive. Keep its wire implementation shared with the lifecycle adapter.
    internal static readonly string ReadOnlyAdapter = Adapter[..Adapter.IndexOf("    probe)", StringComparison.Ordinal)] +
        "    stat) if [[ -e \"$a\" || -L \"$a\" ]]; then value=$(stat -c '%u %a %f %h %d %i' -- \"$a\"); else value=absent; fi ;;\n" +
        "    *) exit 96 ;;\n  esac\n  printf 'ok %s\\n' \"$(printf '%s' \"$value\" | base64 -w0)\"\ndone\n";
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
