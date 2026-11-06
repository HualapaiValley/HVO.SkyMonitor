using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public sealed class HostImageBindingTests
{
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("HVO.SkyMonitor.CameraAgent")]
    [DataRow("HVO.SkyMonitor.LogicHost")]
    public async Task HostImage_ResolvesExistingUrlsWithoutPortOverrideWarning(string hostName)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            root = root.Parent;
        }
        Assert.IsNotNull(root);

        // The pinned ASP.NET runtime image supplies this default. Apply the final
        // stage's ENV assignments exactly as Docker does, without process-wide env changes.
        var configuration = new Dictionary<string, string?>
        {
            ["HTTP_PORTS"] = "8080",
            ["HTTPS_PORTS"] = string.Empty
        };
        var dockerfile = await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", hostName, "Dockerfile")).ConfigureAwait(false);
        var runtime = dockerfile[dockerfile.LastIndexOf("\nFROM ", StringComparison.Ordinal)..];
        foreach (var line in runtime.Replace("\\\n", " ", StringComparison.Ordinal).Split('\n'))
        {
            if (!line.StartsWith("ENV ", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var assignment in line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = assignment.Split('=', 2);
                if (parts[0].StartsWith("ASPNETCORE_", StringComparison.Ordinal))
                {
                    configuration[parts[0]["ASPNETCORE_".Length..]] = parts[1].Trim('"');
                }
            }
        }

        var server = new AddressRecordingServer();
        var logs = new WarningRecorder();
        using var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseConfiguration(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build())
                .UseServer(server)
                .ConfigureLogging(logging => logging.ClearProviders().AddProvider(logs))
                .Configure(_ => { }))
            .Build();

        await host.StartAsync().ConfigureAwait(false);
        try
        {
            Assert.HasCount(1, server.Addresses.Addresses);
            Assert.AreEqual("http://+:8080", server.Addresses.Addresses.Single(),
                "The image and installer must keep their existing wildcard HTTP binding.");
            Assert.IsFalse(logs.Messages.Any(message => message.Contains("Overriding HTTP_PORTS", StringComparison.Ordinal)),
                string.Join(Environment.NewLine, logs.Messages));
        }
        finally
        {
            await host.StopAsync().ConfigureAwait(false);
        }
    }

    private sealed class AddressRecordingServer : IServer
    {
        public ServerAddressesFeature Addresses { get; } = new();
        public IFeatureCollection Features { get; } = new FeatureCollection();

        public AddressRecordingServer() => Features.Set<IServerAddressesFeature>(Addresses);
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class WarningRecorder : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Messages.Enqueue(formatter(state, exception));
            }
        }
        public void Dispose() { }
    }
}
