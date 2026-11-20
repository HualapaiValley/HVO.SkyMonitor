using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;

internal static class ExposureMeasurementServiceCollectionExtensions
{
    internal static void AddExposureMeasurement(this IServiceCollection services)
    {
        services.AddSingleton<ICenteredExposureGraphExtension, ExposureMeasurementGraphPlan>();
        services.AddSingleton<IExposureMeasurementRunReader>(provider => provider.GetRequiredService<CaptureProcessingPersistence>());
        services.AddSingleton(new CaptureProcessingStepRegistration(ExposureMeasurementProducts.StepAlias,
            typeof(ExposureMeasurementCaptureProcessingStep), typeof(ExposureMeasurementStepOptions), 4, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(ExposureAssociationProducts.StepAlias,
            typeof(ExposureStellarAssociationCaptureProcessingStep), typeof(MeasuredStellarAssociationProcessingStepOptions), 16, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(ExposureAstrometryCaptureProcessingStep.CenteredAlias,
            typeof(ExposureAstrometryCaptureProcessingStep), typeof(MeasuredAstrometryCaptureOptions), 19, AutoInclude: false));
    }
}
