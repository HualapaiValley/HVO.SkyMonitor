using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// A scripted Docker CLI for the Silo runtime's Docker-facing checks (#1966): it answers endpoint discovery with a local
/// Unix socket, strips the pinned <c>--host</c>, records every call (and its standard input), and asks the test for the
/// answer. An unanswered call fails like a daemon error, so a check that issues an unexpected command cannot pass.
/// </summary>
internal sealed class ScriptedDocker(Func<IReadOnlyList<string>, string?, ProcessResult?> answer) : IProcessInputRunner
{
    public const string Endpoint = "unix:///var/run/docker.sock";

    public List<(string[] Arguments, string? Input)> Calls { get; } = [];

    /// <summary>For each <c>image load --input PATH</c>, the SHA-256 of the bytes at PATH when Docker would read them.</summary>
    public List<string> LoadedSha256 { get; } = [];

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => Task.FromResult(Run(arguments, null));

    public Task<ProcessResult> RunWithInputAsync(string fileName, IReadOnlyList<string> arguments, string standardInput, CancellationToken cancellationToken)
        => Task.FromResult(Run(arguments, standardInput));

    private ProcessResult Run(IReadOnlyList<string> arguments, string? input)
    {
        if (arguments is ["context", "inspect", ..])
            return Ok(Endpoint);
        string[] args = arguments.Count >= 2 && arguments[0] == "--host" && arguments[1] == Endpoint ? [.. arguments.Skip(2)] : [.. arguments];
        Calls.Add((args, input));
        if (args is ["image", "load", "--input", var path])
            LoadedSha256.Add(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        return answer(args, input) ?? new ProcessResult(1, string.Empty, $"Error response from daemon: unscripted call {string.Join(' ', args)}");
    }

    public bool Called(params string[] prefix) => Calls.Any(call => call.Arguments.Take(prefix.Length).SequenceEqual(prefix));

    public static ProcessResult Ok(string output) => new(0, output, string.Empty);

    public static ProcessResult Ok(JsonNode node) => new(0, node.ToJsonString(), string.Empty);

    public static ProcessResult NoSuch(string what, string name) => new(1, string.Empty, $"Error response from daemon: No such {what}: {name}");

    public static ProcessResult DaemonError() => new(1, string.Empty, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock.");
}
