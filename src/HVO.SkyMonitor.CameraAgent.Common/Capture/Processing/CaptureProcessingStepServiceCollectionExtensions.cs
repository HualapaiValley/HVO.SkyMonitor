using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public static class CaptureProcessingStepServiceCollectionExtensions
{
    public static IServiceCollection AddCaptureProcessingStep<TStep, TOptions>(
        this IServiceCollection services,
        string? alias = null,
        int defaultOrder = 0)
        where TStep : ConfigurableCaptureProcessingStep<TOptions>
        where TOptions : class, new()
    {
        services.AddTransient<TStep>();
        alias ??= typeof(TStep).FullName ?? typeof(TStep).Name;
        services.AddSingleton(new CaptureProcessingStepRegistration(alias, typeof(TStep), typeof(TOptions), defaultOrder));
        return services;
    }
}
