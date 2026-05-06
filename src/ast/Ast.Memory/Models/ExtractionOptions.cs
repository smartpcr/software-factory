// -----------------------------------------------------------------------
// <copyright file="ExtractionOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Models;

/// <summary>
/// Options controlling the extraction pipeline.
/// Per architecture.md §5.4.
/// </summary>
public class ExtractionOptions
{
    public IReadOnlyList<string> Languages { get; init; } = new[] { "C#", "PowerShell" };
    public string? CommitSha { get; init; }
    public int? CommitIndex { get; init; }
    public bool IncludeTestProjects { get; init; }
    public bool GenerateEmbeddings { get; init; } = true;
}