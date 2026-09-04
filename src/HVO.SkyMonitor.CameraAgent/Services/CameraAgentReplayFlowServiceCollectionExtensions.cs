using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Replay;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>Registers the archive-to-replay operator flow and its read-only runner facts.</summary>
internal static class CameraAgentReplayFlowServiceCollectionExtensions
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A replay runner transport that cannot be constructed renders as not yet observed.")]
    public static IServiceCollection AddCameraAgentReplayFlow(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ICameraAgentReplayUiService, CameraAgentReplayUiService>();
        services.AddScoped(provider =>
        {
            var hostOptions = provider.GetRequiredService<IOptions<CameraAgentHostOptions>>();
            LocalReplayRunnerClient? runner = null;
            if (hostOptions.Value.ProcessingGraphs.ReplayProfile == ReplayExecutionProfile.LocalRunner)
            {
                try
                {
                    runner = provider.GetService<LocalReplayRunnerClient>();
                }
                catch (Exception)
                {
                    runner = null;
                }
            }
            return new CameraAgentReplayRunnerFactsProjection(hostOptions, runner);
        });
        return services;
    }
}
