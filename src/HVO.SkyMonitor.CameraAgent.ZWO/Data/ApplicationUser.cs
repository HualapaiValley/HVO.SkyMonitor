using System.Collections.Generic;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.CameraAgent.ZWO.Data;

// Add profile data for application users by adding properties to the ApplicationUser class
public class ApplicationUser : IdentityUser
{
	public ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();
}

