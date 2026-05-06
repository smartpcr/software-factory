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
}