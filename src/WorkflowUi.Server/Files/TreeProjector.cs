// -----------------------------------------------------------------------
// <copyright file="TreeProjector.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Files;

using System;
using System.Collections.Generic;
using System.Linq;
using WorkflowUi.Server.Persistence;

/// <summary>
/// Projects a flat list of workflow summaries into a nested tree structure
/// based on slash-separated workflow ids.
/// </summary>
public static class TreeProjector
{
    /// <summary>
    /// Converts flat workflow summaries (with /-delimited ids) into a tree.
    /// </summary>
    public static TreeNode Project(IReadOnlyList<WorkflowSummary> summaries)
    {
        var root = new TreeNode { Name = "", IsFolder = true };

        foreach (var summary in summaries)
        {
            var segments = summary.Id.Split('/');
            var current = root;

            for (int i = 0; i < segments.Length - 1; i++)
            {
                var folder = current.Children.FirstOrDefault(c => c.Name == segments[i] && c.IsFolder);
                if (folder == null)
                {
                    folder = new TreeNode { Name = segments[i], IsFolder = true };
                    current.Children.Add(folder);
                }

                current = folder;
            }

            current.Children.Add(new TreeNode
            {
                Name = segments[^1],
                IsFolder = false,
                WorkflowId = summary.Id,
                LastModifiedUtc = summary.LastModifiedUtc,
                SizeBytes = summary.SizeBytes,
            });
        }

        return root;
    }
}

/// <summary>
/// Represents a node in the workspace tree.
/// </summary>
public class TreeNode
{
    public string Name { get; set; } = string.Empty;

    public bool IsFolder { get; set; }

    public string? WorkflowId { get; set; }

    public DateTime? LastModifiedUtc { get; set; }

    public long? SizeBytes { get; set; }

    public List<TreeNode> Children { get; set; } = new();
}
