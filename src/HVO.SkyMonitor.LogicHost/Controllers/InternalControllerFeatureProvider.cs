using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace HVO.SkyMonitor.LogicHost.Controllers;

/// <summary>
/// Allows ASP.NET Core MVC to discover <c>internal</c> controllers.
/// This is useful for keeping controller types non-public while still exposing routes.
/// </summary>
internal sealed class InternalControllerFeatureProvider : ControllerFeatureProvider
{
    protected override bool IsController(TypeInfo typeInfo)
    {
        if (!typeInfo.IsClass)
        {
            return false;
        }

        if (typeInfo.IsAbstract)
        {
            return false;
        }

        if (typeInfo.ContainsGenericParameters)
        {
            return false;
        }

        if (typeInfo.IsDefined(typeof(NonControllerAttribute)))
        {
            return false;
        }

        // Match the default convention: *Controller.
        if (!typeInfo.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
