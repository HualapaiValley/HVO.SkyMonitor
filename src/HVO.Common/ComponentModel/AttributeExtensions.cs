using System.ComponentModel;
using System.Reflection;

namespace HVO.ComponentModel;

/// <summary>
/// Provides extension methods for working with attributes on enum values
/// </summary>
public static class AttributeExtensions
{
    /// <summary>
    /// Gets the description attribute value from an enum, or the enum's string representation if no description is found
    /// </summary>
    /// <typeparam name="T">The enum type</typeparam>
    /// <param name="Enum">The enum value to get the description for</param>
    /// <returns>The description from the DescriptionAttribute, or the enum's ToString() value if no description exists</returns>
    public static string? GetDescription<T>(this T Enum) where T : Enum
        => Enum.GetEnumAttribute<T, DescriptionAttribute>()?.Description ?? Enum.ToString();

    /// <summary>
    /// Gets a custom attribute of the specified type from an enum value
    /// </summary>
    /// <typeparam name="TEnum">The enum type</typeparam>
    /// <typeparam name="TAttribute">The attribute type to retrieve</typeparam>
    /// <param name="Enum">The enum value to get the attribute from</param>
    /// <returns>The attribute instance if found, otherwise null</returns>
    public static TAttribute? GetEnumAttribute<TEnum, TAttribute>(this TEnum Enum)
        where TEnum : Enum
        where TAttribute : Attribute
    {
        var MemberInfo = typeof(TEnum).GetMember(Enum.ToString());
        if (MemberInfo.Length == 0)
            return null;

        return MemberInfo[0].GetCustomAttribute<TAttribute>();
    }
}