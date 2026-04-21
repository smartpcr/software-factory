// -----------------------------------------------------------------------
// <copyright file="EnumMemberHelper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Enums;

using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Serialization;

/// <summary>
/// Resolves enum values from <see cref="EnumMemberAttribute"/> string values
/// and converts enum values back to their wire-format strings.
/// Falls back to <c>Enum.Parse</c> when no attribute matches.
/// </summary>
public static class EnumMemberHelper
{
    private static readonly ConcurrentDictionary<Type, Dictionary<string, object>> ValueToEnum = new();
    private static readonly ConcurrentDictionary<Type, Dictionary<object, string>> EnumToValue = new();

    /// <summary>
    /// Parses a string into an enum value, checking <see cref="EnumMemberAttribute"/>
    /// values first, then falling back to <c>Enum.Parse</c>.
    /// </summary>
    public static object ParseEnumMember(Type enumType, string value)
    {
        var map = ValueToEnum.GetOrAdd(enumType, BuildValueToEnumMap);
        if (map.TryGetValue(value, out var result))
        {
            return result;
        }

        return Enum.Parse(enumType, value, ignoreCase: true);
    }

    /// <summary>
    /// Parses a string into a typed enum value.
    /// </summary>
    public static T ParseEnumMember<T>(string value) where T : struct, Enum
    {
        return (T)ParseEnumMember(typeof(T), value);
    }

    /// <summary>
    /// Converts an enum value to its <see cref="EnumMemberAttribute"/> string,
    /// or falls back to <c>ToString()</c>.
    /// </summary>
    public static string ToEnumMemberValue(Type enumType, object value)
    {
        var map = EnumToValue.GetOrAdd(enumType, BuildEnumToValueMap);
        return map.TryGetValue(value, out var result) ? result : value.ToString()!;
    }

    /// <summary>
    /// Converts a typed enum value to its wire-format string.
    /// </summary>
    public static string ToEnumMemberValue<T>(T value) where T : struct, Enum
    {
        return ToEnumMemberValue(typeof(T), value);
    }

    private static Dictionary<string, object> BuildValueToEnumMap(Type enumType)
    {
        var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attr = field.GetCustomAttribute<EnumMemberAttribute>();
            var enumVal = field.GetValue(null)!;
            if (attr?.Value != null)
            {
                map[attr.Value] = enumVal;
            }

            // Also register the member name so Enum.Parse-style lookups work
            map[field.Name] = enumVal;
        }

        return map;
    }

    private static Dictionary<object, string> BuildEnumToValueMap(Type enumType)
    {
        var map = new Dictionary<object, string>();
        foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attr = field.GetCustomAttribute<EnumMemberAttribute>();
            var enumVal = field.GetValue(null)!;
            map[enumVal] = attr?.Value ?? field.Name;
        }

        return map;
    }
}