// -----------------------------------------------------------------------
// <copyright file="GitDiffDriverSteps.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests.Steps;

using Ast.Memory.Models;
using Ast.Memory.Sync;
using FluentAssertions;
using Reqnroll;

[Binding]
public class GitDiffDriverSteps
{
    private string gitDiffOutput = string.Empty;
    private IReadOnlyList<FileChange> parsedChanges = Array.Empty<FileChange>();

    [Given(@"git diff output with ""(.*)"" status for ""(.*)"" and ""(.*)"" status for ""(.*)""")]
    public void GivenGitDiffOutputWithStatusForAndStatusFor(string status1, string path1, string status2, string path2)
    {
        // NUL-delimited format: STATUS\0path\0STATUS\0path\0
        this.gitDiffOutput = $"{status1}\0{path1}\0{status2}\0{path2}\0";
    }

    [Given(@"git diff output with rename from ""(.*)"" to ""(.*)"" with similarity (\d+)")]
    public void GivenGitDiffOutputWithRename(string oldPath, string newPath, int similarity)
    {
        this.gitDiffOutput = $"R{similarity}\0{oldPath}\0{newPath}\0";
    }

    [When(@"the output is parsed")]
    public void WhenTheOutputIsParsed()
    {
        this.parsedChanges = GitDiffDriver.ParseNameStatusOutput(this.gitDiffOutput);
    }

    [Then(@"the result contains (Modified|Deleted|Added|Renamed) for ""(.*)""")]
    public void ThenTheResultContainsStatusForPath(string status, string path)
    {
        var expectedStatus = Enum.Parse<FileChangeStatus>(status);
        this.parsedChanges.Should().Contain(c => c.Status == expectedStatus && c.Path == path);
    }

    [Then(@"the result contains a rename for ""(.*)"" from ""(.*)""")]
    public void ThenTheResultContainsRenamedWithOldPath(string newPath, string oldPath)
    {
        this.parsedChanges.Should().Contain(c =>
            c.Status == FileChangeStatus.Renamed &&
            c.Path == newPath &&
            c.OldPath == oldPath);
    }
}