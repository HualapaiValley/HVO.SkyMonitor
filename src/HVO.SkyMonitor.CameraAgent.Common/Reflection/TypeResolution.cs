using System;
using System.Linq;
using System.Reflection;

namespace HVO.SkyMonitor.CameraAgent.Common.Reflection;

internal static class TypeResolution
{
    public static Type ResolveRequired(string typeName, Type assignableTo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(assignableTo);

        var type = TryResolve(typeName);
        if (type is null)
        {
            throw new InvalidOperationException($"Unable to resolve type '{typeName}'. Ensure the assembly is loaded and the type name is correct.");
        }

        if (!assignableTo.IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"Type '{type.FullName}' does not implement required contract '{assignableTo.FullName}'.");
        }

        return type;
    }

    public static Type? TryResolve(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);

        var type = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
        if (type is not null)
        {
            return type;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }
}
