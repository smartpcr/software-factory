// -----------------------------------------------------------------------
// <copyright file="GitWorktreeOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Git;

/// <summary>
/// Bound from the <c>Forge:Git</c> configuration section. Worktrees may
/// only be created or removed underneath <see cref="WorktreeRoot"/>; any
/// path that escapes the root is rejected with <see cref="UnauthorizedAccessException"/>.
/// </summary>
public class GitWorktreeOptions
{
    public const string SectionName = "Forge:Git";

    /// <summary>
    /// Repository root that owns the worktrees. Defaults to the API working
    /// directory; override via <c>Forge:Git:RepoPath</c>.
    /// </summary>
    public string RepoPath { get; set; } = string.Empty;

    /// <summary>
    /// Filesystem prefix that <see cref="IGitWorktreeService.CreateAsync"/>
    /// requires every new worktree path to live under. Tests use a temp
    /// directory; production uses something like <c>C:\forge\atlas\wt</c>.
    /// </summary>
    public string WorktreeRoot { get; set; } = string.Empty;
}
