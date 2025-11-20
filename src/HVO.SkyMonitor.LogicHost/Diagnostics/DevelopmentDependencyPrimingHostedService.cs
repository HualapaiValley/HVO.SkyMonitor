using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.LogicHost.Diagnostics;

/// <summary>
/// Development-only hosted service that triggers dependency priming after the app starts.
/// </summary>
internal sealed class DevelopmentDependencyPrimingHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<DevelopmentDependencyPrimingHostedService> _logger;
    private Task? _primingTask;

    public DevelopmentDependencyPrimingHostedService(
        IServiceScopeFactory scopeFactory,
        IHostEnvironment environment,
        ILogger<DevelopmentDependencyPrimingHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _environment = environment;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            _logger.LogDebug("Skipping dependency priming for environment {Environment}", _environment.EnvironmentName);
            return Task.CompletedTask;
        }

        _primingTask = Task.Run(() => ExecuteAsync(cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_primingTask is null)
        {
            return;
        }

        try
        {
            await Task.WhenAny(_primingTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignored: the task is best-effort during shutdown.
        }
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var primer = scope.ServiceProvider.GetRequiredService<DependencyPrimer>();
            await primer.PrimeAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Completed dependency priming for development environment.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Dependency priming encountered an error.");
        }
    }
}
