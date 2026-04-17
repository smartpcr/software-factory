// -----------------------------------------------------------------------
// <copyright file="ETagComputer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Persistence;

using System;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Computes ETag values as SHA-256(yamlBytes || sidecarBytes) hex-encoded.
/// </summary>
public static class ETagComputer
{
    /// <summary>
    /// Computes the ETag for a YAML + sidecar pair.
    /// </summary>
    public static string Compute(string yaml, string sidecarJson)
    {
        var yamlBytes = Encoding.UTF8.GetBytes(yaml ?? string.Empty);
        var sidecarBytes = Encoding.UTF8.GetBytes(sidecarJson ?? string.Empty);

        var combined = new byte[yamlBytes.Length + sidecarBytes.Length];
        Buffer.BlockCopy(yamlBytes, 0, combined, 0, yamlBytes.Length);
        Buffer.BlockCopy(sidecarBytes, 0, combined, yamlBytes.Length, sidecarBytes.Length);

        var hash = SHA256.HashData(combined);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
