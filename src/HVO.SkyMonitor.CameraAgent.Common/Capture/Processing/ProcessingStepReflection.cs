using System;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal static class ProcessingStepReflection
{
    public static Type GetOptionsType(Type stepType)
    {
        ArgumentNullException.ThrowIfNull(stepType);
        var current = stepType;
        while (current is not null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ConfigurableCaptureProcessingStep<>))
            {
                return current.GetGenericArguments()[0];
            }

            current = current.BaseType;
        }

        throw new InvalidOperationException($"Processing step type '{stepType.FullName}' must inherit from ConfigurableCaptureProcessingStep<TOptions>.");
    }
}
