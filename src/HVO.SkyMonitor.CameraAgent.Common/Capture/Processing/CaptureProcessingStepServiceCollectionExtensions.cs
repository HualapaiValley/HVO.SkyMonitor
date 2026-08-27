using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public static class CaptureProcessingStepServiceCollectionExtensions
{
    public static IServiceCollection AddCaptureProcessingStep<TStep, TOptions>(
        this IServiceCollection services,
        string alias,
        int defaultOrder = 0)
        where TStep : ConfigurableCaptureProcessingStep<TOptions>
        where TOptions : class, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        services.AddTransient<TStep>();
        services.AddSingleton(new CaptureProcessingStepRegistration(alias, typeof(TStep), typeof(TOptions), defaultOrder));
        return services;
    }
}
