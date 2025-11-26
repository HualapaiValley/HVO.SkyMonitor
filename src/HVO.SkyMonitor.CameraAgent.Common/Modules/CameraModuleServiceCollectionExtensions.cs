using HVO.SkyMonitor.AgentCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules;

public static class CameraModuleServiceCollectionExtensions
{
    public static IServiceCollection AddCameraModule<TModule>(this IServiceCollection services, string moduleType)
        where TModule : class, ICameraModule
    {
        services.AddTransient<TModule>();
        services.AddSingleton(new CameraModuleRegistration(moduleType, typeof(TModule)));
        return services;
    }
}
