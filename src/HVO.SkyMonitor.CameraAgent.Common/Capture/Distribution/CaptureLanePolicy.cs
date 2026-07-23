using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

internal sealed class CaptureLanePolicy
{
    public CaptureLanePolicy(IOptions<CameraAgentHostOptions> options)
    {
        var configured = options.Value.CaptureDistribution;
        var definitions = new List<CaptureLaneDefinition>
        {
            Create("standard", enabled: true, required: true, ordered: true)
        };
        definitions.Add(Create(
            "upload",
            options.Value.CentralIntegration.Mode == CentralIntegrationMode.Enabled && configured.UploadEnabled,
            required: true,
            ordered: false));
        var transient = options.Value.TransientDetection;
        if (transient.Mode is TransientOperatingMode.Edge or TransientOperatingMode.Hybrid)
        {
            definitions.Add(Create("transient", enabled: true, transient.Required, ordered: true));
        }
        definitions.AddRange(configured.SecondaryLanes.Select(static lane =>
            Create(lane.Name, lane.Enabled, lane.Required, ordered: true)));
        Definitions = definitions;
    }

    internal IReadOnlyList<CaptureLaneDefinition> Definitions { get; }

    private static CaptureLaneDefinition Create(string name, bool enabled, bool required, bool ordered)
    {
        var policy = $"v1\n{name}\n{enabled}\n{required}\n{ordered}";
        return new CaptureLaneDefinition(
            name,
            enabled,
            required,
            ordered,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(policy))));
    }
}
