using System.Security.Claims;
using HVO;
using HVO.SkyMonitor.CameraAgent.ZWO.Models.Sample;

namespace HVO.SkyMonitor.CameraAgent.ZWO.Services;

/// <summary>
/// Provides diagnostic responses for the sample API endpoints.
/// </summary>
public interface ISampleStatusService
{
    Result<SampleStatusResponse> GetStatus();

    Result<SampleAuthenticatedResponse> GetAuthenticatedStatus(ClaimsPrincipal principal);
}
