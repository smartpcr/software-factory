// -----------------------------------------------------------------------
// <copyright file="Phase24MustReplyTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System;
using System.Linq;
using Forge.Api.Services;
using Xunit;

/// <summary>
/// Phase 24.18 — "no code change needed" must-reply rule (Q24.13). The
/// runner registers <c>post_pr_comment_reply</c> for comment-response runs;
/// the rubric language is surfaced via the system template (Phase 24.17
/// covers the template content); this test class focuses on the
/// observable contract: AllowedTools list, evaluator read-only toolset,
/// and the open_pr exclusion.
/// </summary>
public class Phase24MustReplyTests
{
    [Fact]
    public void CommentResponse_AllowedTools_Includes_PostPrCommentReply()
    {
        // Q24.13 — the new tool is registered alongside the standard
        // read/write/git toolset.
        Assert.Contains("post_pr_comment_reply", CrewQueueWorker.CommentResponseAllowedTools);
    }

    [Fact]
    public void CommentResponse_AllowedTools_Includes_Standard_Read_Write_Git()
    {
        // Q24.13 — the agent still needs the standard toolset to commit
        // fixes; reply-or-fix is the choice it makes per comment.
        var tools = CrewQueueWorker.CommentResponseAllowedTools;
        Assert.Contains("read_file", tools);
        Assert.Contains("write_file", tools);
        Assert.Contains("git_commit", tools);
        Assert.Contains("git_push", tools);
        Assert.Contains("run_command", tools);
    }

    [Fact]
    public void CommentResponse_AllowedTools_Excludes_OpenPr()
    {
        // Q24.12 — comment-response writes against the EXISTING PR. open_pr
        // is intentionally absent from the list so the agent can't accidentally
        // open a duplicate PR.
        Assert.DoesNotContain("open_pr", CrewQueueWorker.CommentResponseAllowedTools);
    }

    [Fact]
    public void Evaluator_AllowedTools_Is_Read_Only()
    {
        // Q24.12 — the evaluator scores; it never acts on the PR. No commit /
        // push / reply tools, regardless of kind.
        var tools = CrewQueueWorker.EvaluatorAllowedTools;
        Assert.Contains("read_file", tools);
        Assert.Contains("run_command", tools);
        Assert.DoesNotContain("write_file", tools);
        Assert.DoesNotContain("git_commit", tools);
        Assert.DoesNotContain("git_push", tools);
        Assert.DoesNotContain("post_pr_comment_reply", tools);
        Assert.DoesNotContain("open_pr", tools);
    }

    [Fact]
    public void CommentResponse_AllowedTools_Has_No_Duplicates()
    {
        // Cosmetic but important — duplicate names in the AllowedTools list
        // would be a sign that someone added the same tool twice on a merge.
        var tools = CrewQueueWorker.CommentResponseAllowedTools;
        var distinct = tools.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(tools.Length, distinct.Length);
    }
}
