using System.Diagnostics;

namespace HVO.SkyMonitor.Deployment;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

internal sealed class ProcessRunner(int? maximumOutputCharacters = null) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
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
            if (!outputTask.IsCompleted || !errorTask.IsCompleted) Kill(process);
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
