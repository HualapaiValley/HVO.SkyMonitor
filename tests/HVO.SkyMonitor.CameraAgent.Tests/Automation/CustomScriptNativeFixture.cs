using System.Diagnostics;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

internal static class CustomScriptNativeFixture
{
    internal static async Task<byte[]> CompileAsync(bool preload)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-script-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Path.Combine(root, preload ? "preload.so" : "probe");
            var info = new ProcessStartInfo("/usr/bin/gcc")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            info.Environment.Clear();
            info.Environment["PATH"] = "/usr/bin:/bin";
            foreach (var argument in new[] { "-nostdlib", "-Os", "-fno-stack-protector", "-Wl,--build-id=none,-z,noseparate-code",
                preload ? "-shared" : "-static", "-fPIC", preload ? "-DPRELOAD" : "-DPROBE",
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "scripts", "sandbox-native-probe.c"), "-o", output })
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            Assert.AreEqual(0, process.ExitCode, "The bounded native qualification fixture must compile.");
            return await File.ReadAllBytesAsync(output).ConfigureAwait(false);
        }
        finally { Directory.Delete(root, true); }
    }

    internal static string WritePayload(byte[] bytes, string file) =>
        "printf '" + string.Concat(bytes.Select(static value => "\\" + Convert.ToString(value, 8).PadLeft(3, '0'))) +
        "' > " + file + "; ";
}
