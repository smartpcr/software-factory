// -----------------------------------------------------------------------
// <copyright file="GitIgnoredPathException.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

/// <summary>
/// Thrown by <see cref="RealStoryGitOps"/> when a commit attempt would silently
/// drop one or more files because they are excluded by the repo's
/// <c>.gitignore</c>.
///
/// <para>The trigger condition: a story-driven path (plan doc, workstreams
/// manifest, etc.) is being committed under e.g. <c>docs/stories/...</c>, but
/// the repo's <c>.gitignore</c> is an allowlist policy (OneBranch / SDL repos
/// commonly use <c>/*</c> + <c>!/src/</c>) that excludes the directory. Without
/// this preflight, <c>git add</c> silently skips the file, <c>git status
/// --porcelain</c> returns empty, the commit becomes a no-op, the push pushes
/// nothing, and the PR opens against an unchanged branch. ADO completes the
/// "empty" PR, <c>PrSyncJob</c> marks the row merged, and downstream gates
/// (e.g. <c>PlanArtifactsLandedAsync</c>) silently defer forever waiting for
/// files that will never land.</para>
///
/// <para>Resolution surface in the message: the operator should either (a)
/// set the repo's <c>PlanDocBaseDir</c> to a path the <c>.gitignore</c>
/// permits (e.g. <c>src/docs/stories</c> for OneBranch repos), or (b) add an
/// exception (<c>!{pattern}</c>) on the target branch.</para>
/// </summary>
public sealed class GitIgnoredPathException : InvalidOperationException
{
    /// <summary>The branch on which the commit was attempted.</summary>
    public string Branch { get; }

    /// <summary>The repo-relative paths that were rejected by <c>.gitignore</c>.</summary>
    public IReadOnlyList<string> IgnoredPaths { get; }

    /// <summary>Raw output from <c>git check-ignore -v --</c> (one line per
    /// ignored path: <c>&lt;source&gt;:&lt;linenum&gt;:&lt;pattern&gt;\t&lt;path&gt;</c>).
    /// Preserved verbatim so the operator can see exactly which rule fired.</summary>
    public string GitCheckIgnoreOutput { get; }

    public GitIgnoredPathException(
        string branch,
        IReadOnlyList<string> ignoredPaths,
        string gitCheckIgnoreOutput)
        : base(BuildMessage(branch, ignoredPaths, gitCheckIgnoreOutput))
    {
        this.Branch = branch;
        this.IgnoredPaths = ignoredPaths;
        this.GitCheckIgnoreOutput = gitCheckIgnoreOutput;
    }

    private static string BuildMessage(
        string branch, IReadOnlyList<string> ignoredPaths, string checkIgnoreOutput)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Refusing to commit on '").Append(branch).Append("': ")
          .Append(ignoredPaths.Count).Append(" path(s) excluded by .gitignore. ");
        sb.Append("Configure repo.PlanDocBaseDir to a path the .gitignore allows ")
          .Append("(e.g. 'src/docs/stories' for OneBranch repos that allow only '/src/'), ")
          .Append("or add an exception rule on the target branch. ");
        sb.Append("git check-ignore -v output:\n").Append(checkIgnoreOutput.TrimEnd());
        return sb.ToString();
    }
}
