using System.Diagnostics;

namespace HVO.SkyMonitor.Deployment;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

internal interface IProcessInputRunner : IProcessRunner
{
    Task<ProcessResult> RunWithInputAsync(string fileName, IReadOnlyList<string> arguments, string standardInput, CancellationToken cancellationToken);
}

internal sealed class ProcessRunner(int? maximumOutputCharacters = null) : IProcessInputRunner
{
    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunCoreAsync(fileName, arguments, null, cancellationToken);

    public Task<ProcessResult> RunWithInputAsync(string fileName, IReadOnlyList<string> arguments, string standardInput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(standardInput);
        if (standardInput.Length > 32 * 1024 * 1024) throw new InstallerException("Required command input exceeded its configured limit.");
        return RunCoreAsync(fileName, arguments, standardInput, cancellationToken);
    }

    private async Task<ProcessResult> RunCoreAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.Environment.Remove("HVO_GITHUB_TOKEN");

        try
        {
            if (!process.Start())
            {
                throw new InstallerException($"Could not start required command '{fileName}'.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InstallerException($"Required command '{fileName}' is unavailable.", exception);
        }

        Action stop = () => Kill(process);
        var outputTask = ReadOutputAsync(process.StandardOutput, stop, timeout.Token);
        var errorTask = ReadOutputAsync(process.StandardError, stop, timeout.Token);
        try
        {
            try
            {
                if (standardInput is not null)
                {
                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeout.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                throw new InstallerException($"Required command '{fileName}' exceeded its ten-minute deadline.");
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }
            return new ProcessResult(
                process.ExitCode,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false));
        }
        finally
        {
            // A failed input write can leave a child alive after it closed both output pipes.
            Kill(process);
            try { await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or InstallerException)
            {
                // The command path above reports the original failure. Both pipe readers must finish before
                // their process and cancellation source are disposed, including an output-limit failure.
            }
        }
    }

    private async Task<string> ReadOutputAsync(StreamReader reader, Action stop, CancellationToken token)
    {
        if (maximumOutputCharacters is null) return await reader.ReadToEndAsync(token).ConfigureAwait(false);
        var output = new System.Text.StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return output.ToString();
            if (output.Length + count > maximumOutputCharacters)
            {
                stop();
                throw new InstallerException("Required command output exceeded its configured limit.");
            }
            output.Append(buffer, 0, count);
        }
    }

    private static void Kill(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }
}
