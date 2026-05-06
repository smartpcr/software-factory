// -----------------------------------------------------------------------
// <copyright file="MetadataHelper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Shared helper for reading strongly-typed values from metadata dictionaries.
/// </summary>
internal static class MetadataHelper
{
    internal static string GetRequiredString(Dictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
        {
            throw new KeyNotFoundException($"Required metadata key '{key}' is missing or null.");
        }

        return value.ToString()!;
    }

    internal static string? GetOptionalString(Dictionary<string, object?> metadata, string key)
    {
        if (metadata.TryGetValue(key, out var value) && value is not null)
        {
            return value.ToString();
        }

        return null;
    }

    internal static double GetOptionalDouble(Dictionary<string, object?> metadata, string key, double defaultValue = 0.0)
    {
        if (metadata.TryGetValue(key, out var value) && value is not null)
        {
            return value switch
            {
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                string s when double.TryParse(s, out var parsed) => parsed,
                _ => Convert.ToDouble(value)
            };
        }

        return defaultValue;
    }
}