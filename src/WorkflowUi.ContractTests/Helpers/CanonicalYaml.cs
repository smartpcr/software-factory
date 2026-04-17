// -----------------------------------------------------------------------
// <copyright file="CanonicalYaml.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests.Helpers;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Canonicalises YAML for golden-file comparison.
/// Strips comments, normalises line endings to LF, trims trailing whitespace,
/// replaces tabs with 4 spaces, strips BOM.
/// Does NOT re-sort keys — preserves engine key order.
/// </summary>
public static class CanonicalYaml
{
    /// <summary>
    /// Produces a canonical form of the given YAML string.
    /// </summary>
    public static string Canonicalize(string yaml)
    {
        if (string.IsNullOrEmpty(yaml))
        {
            return string.Empty;
        }

        // Strip BOM
        yaml = yaml.TrimStart('\uFEFF');

        // Normalise line endings to LF
        yaml = yaml.Replace("\r\n", "\n").Replace("\r", "\n");

        // Replace tabs with 4 spaces
        yaml = yaml.Replace("\t", "    ");

        var lines = yaml.Split('\n');
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            // Strip full-line comments (lines whose first non-whitespace char is #)
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            // Trim trailing whitespace
            var clean = line.TrimEnd();
            sb.Append(clean);
            sb.Append('\n');
        }

        // Remove trailing blank lines
        var result = sb.ToString().TrimEnd('\n');
        return result + "\n";
    }

    /// <summary>
    /// Compares two YAML strings after canonicalisation.
    /// </summary>
    public static bool Equal(string a, string b)
    {
        return string.Equals(
            Canonicalize(a),
            Canonicalize(b),
            System.StringComparison.Ordinal);
    }
}
