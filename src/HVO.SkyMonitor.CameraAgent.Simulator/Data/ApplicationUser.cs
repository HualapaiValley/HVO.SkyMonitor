using System.Collections.Generic;
using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.CameraAgent.Simulator.Data;

// Add profile data for application users by adding properties to the ApplicationUser class
public class ApplicationUser : IdentityUser
{
	public ICollection<ApplicationUserApiKey> ApiKeys { get; set; } = new List<ApplicationUserApiKey>();
}

