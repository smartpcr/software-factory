// -----------------------------------------------------------------------
// <copyright file="ZipEntryNameSanitiser.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Files;

using System;
using System.IO;

/// <summary>
/// Sanitises zip entry names to prevent zip-slip attacks.
/// </summary>
public static class ZipEntryNameSanitiser
{
    /// <summary>
    /// Returns true if the entry name is safe (no path traversal, no absolute paths, no drive letters).
    /// </summary>
    public static bool IsSafe(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return false;
        }

        // Reject path traversal
        if (entryName.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        // Reject absolute paths
        if (Path.IsPathRooted(entryName))
        {
            return false;
        }

        // Reject drive letters (Windows)
        if (entryName.Length >= 2 && entryName[1] == ':')
        {
            return false;
        }

        // Reject leading slashes
        if (entryName.StartsWith('/') || entryName.StartsWith('\\'))
        {
            return false;
        }

        return true;
    }
}
