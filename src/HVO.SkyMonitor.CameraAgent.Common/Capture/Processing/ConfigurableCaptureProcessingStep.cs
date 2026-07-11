using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public abstract class ConfigurableCaptureProcessingStep<TOptions> : ICaptureProcessingStep
    where TOptions : class, new()
{
    protected ConfigurableCaptureProcessingStep(
        CaptureProcessingStepMetadata metadata,
        TOptions options)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Validator.ValidateObject(Options, new ValidationContext(Options), validateAllProperties: true);
    }

    protected CaptureProcessingStepMetadata Metadata { get; }

    protected TOptions Options { get; }

    public string Name => Metadata.Id;

    public int Order => Metadata.Order;

    public abstract ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken);
}
