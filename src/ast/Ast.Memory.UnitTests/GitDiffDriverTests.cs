// -----------------------------------------------------------------------
// <copyright file="GitDiffDriverTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ast.Memory.Sync;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class GitDiffDriverTests
{
    // ───────────────────────────────────────────────────────────────────
    // Parser-level unit tests (synthetic output, no git repo needed)
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseNameStatusOutput_DetectsAddedModifiedDeleted()
    {
        var output = "A\tNewFile.cs\nM\tService.cs\nD\tOldFile.cs\n";

        var result = GitDiffDriver.ParseNameStatusOutput(output);

        result.Should().HaveCount(3);
        result[0].Kind.Should().Be(FileChangeKind.Added);
        result[0].Path.Should().Be("NewFile.cs");
        result[1].Kind.Should().Be(FileChangeKind.Modified);
        result[1].Path.Should().Be("Service.cs");
        result[2].Kind.Should().Be(FileChangeKind.Deleted);
        result[2].Path.Should().Be("OldFile.cs");
    }

    [TestMethod]
    public void ParseNameStatusOutput_RenameDetection()
    {
        var output = "R095\tOld.cs\tNew.cs\n";

        var result = GitDiffDriver.ParseNameStatusOutput(output);

        result.Should().HaveCount(1);
        var rename = result[0];
        rename.Kind.Should().Be(FileChangeKind.Renamed);
        rename.OldPath.Should().Be("Old.cs");
        rename.Path.Should().Be("New.cs");
        rename.SimilarityPercent.Should().Be(95);
    }

    [TestMethod]
    public void ParseNameStatusOutput_SkipsBinaryFiles()
    {
        var output = "A\timage.png\nM\tService.cs\nA\tdata.bin\n";
        var binaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image.png", "data.bin" };

        var result = GitDiffDriver.ParseNameStatusOutput(output, binaryFiles: binaries);

        result.Should().ContainSingle();
        result[0].Path.Should().Be("Service.cs");
    }

    [TestMethod]
    public void ParseNameStatusOutput_SkipsSubmodulePaths()
    {
        var output = "M\texternal/lib\nM\tsrc/App.cs\nA\texternal/lib/file.txt\n";
        var submodules = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "external/lib" };

        var result = GitDiffDriver.ParseNameStatusOutput(output, submodulePaths: submodules);

        result.Should().ContainSingle();
        result[0].Path.Should().Be("src/App.cs");
    }

    [TestMethod]
    public void ParseNameStatusOutput_SkipsGitmodulesFile()
    {
        var output = "A\t.gitmodules\nM\tsrc/App.cs\n";

        var result = GitDiffDriver.ParseNameStatusOutput(output);

        result.Should().ContainSingle();
        result[0].Path.Should().Be("src/App.cs");
    }

    [TestMethod]
    public void ParseNameStatusOutput_EmptyOutput_ReturnsEmpty()
    {
        GitDiffDriver.ParseNameStatusOutput("").Should().BeEmpty();
        GitDiffDriver.ParseNameStatusOutput("   \n  \n").Should().BeEmpty();
    }

    [TestMethod]
    public void ParseNameStatusOutput_UnknownStatusIsSkipped()
    {
        var output = "T\tsome-file\nM\treal.cs\n";

        var result = GitDiffDriver.ParseNameStatusOutput(output);

        result.Should().ContainSingle();
        result[0].Path.Should().Be("real.cs");
    }

    [TestMethod]
    public void ParseRawDiffForSubmodules_DetectsGitlinkMode()
    {
        var rawOutput = string.Join("\n",
            ":160000 160000 abc1234 def5678 M\tvendor/lib",
            ":100644 100644 aaa1111 bbb2222 M\tsrc/Code.cs",
            ":000000 160000 0000000 ccc3333 A\tvendor/new-sub",
            ":160000 000000 ddd4444 0000000 D\tvendor/old-sub",
            "");

        var submodules = GitDiffDriver.ParseRawDiffForSubmodules(rawOutput);

        submodules.Should().Contain("vendor/lib");
        submodules.Should().Contain("vendor/new-sub");
        submodules.Should().Contain("vendor/old-sub");
        submodules.Should().NotContain("src/Code.cs");
    }

    [TestMethod]
    public void ParseRawDiffForSubmodules_EmptyOutput_ReturnsEmpty()
    {
        GitDiffDriver.ParseRawDiffForSubmodules("").Should().BeEmpty();
    }

    [TestMethod]
    public void ParseNumstatForBinaries_DetectsBinaryDash()
    {
        var output = "-\t-\timage.png\n10\t5\tCode.cs\n-\t-\tdata.bin\n";

        var binaries = GitDiffDriver.ParseNumstatForBinaries(output);

        binaries.Should().Contain("image.png");
        binaries.Should().Contain("data.bin");
        binaries.Should().NotContain("Code.cs");
    }

    [TestMethod]
    public void ParseRenameNumstatPath_BraceSyntax()
    {
        var (oldPath, newPath) = GitDiffDriver.ParseRenameNumstatPath("dir/{old.cs => new.cs}");
        oldPath.Should().Be("dir/old.cs");
        newPath.Should().Be("dir/new.cs");
    }

    [TestMethod]
    public void ParseRenameNumstatPath_NoBraces()
    {
        var (oldPath, newPath) = GitDiffDriver.ParseRenameNumstatPath("old.cs => new.cs");
        oldPath.Should().Be("old.cs");
        newPath.Should().Be("new.cs");
    }

    [TestMethod]
    public void Constructor_RejectsEmptyRepoRoot()
    {
        Action act = () => new GitDiffDriver("");
        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public async Task GetChangedFilesAsync_RejectsEmptyCommits()
    {
        var driver = new GitDiffDriver("C:\\fake");

        Func<Task> act1 = () => driver.GetChangedFilesAsync("", "HEAD");
        await act1.Should().ThrowAsync<ArgumentException>();

        Func<Task> act2 = () => driver.GetChangedFilesAsync("HEAD~1", "");
        await act2.Should().ThrowAsync<ArgumentException>();
    }

    [TestMethod]
    public void FileChange_ToString_NonRename()
    {
        var change = new FileChange { Kind = FileChangeKind.Modified, Path = "src/App.cs" };
        change.ToString().Should().Be("Modified: src/App.cs");
    }

    [TestMethod]
    public void FileChange_ToString_Rename()
    {
        var change = new FileChange
        {
            Kind = FileChangeKind.Renamed,
            OldPath = "Old.cs",
            Path = "New.cs",
            SimilarityPercent = 95
        };

        change.ToString().Should().Be("Renamed: Old.cs → New.cs (95%)");
    }

    // ───────────────────────────────────────────────────────────────────
    // Deterministic temp-git-repo acceptance tests
    // These create a real git repo on disk and exercise
    // GitDiffDriver.GetChangedFiles(A, B) end-to-end.
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Acceptance scenario: Given commits A and B where Service.cs was modified
    /// and OldFile.cs was deleted, When GitDiffDriver.GetChangedFiles(A, B) is
    /// called, Then result contains (Modified, "Service.cs") and (Deleted, "OldFile.cs").
    /// </summary>
    [TestMethod]
    public async Task GetChangedFiles_DiffDetection_ModifiedAndDeleted()
    {
        using var repo = new TempGitRepo();

        // Commit A: create Service.cs and OldFile.cs
        repo.WriteFile("Service.cs", "public class Service { }");
        repo.WriteFile("OldFile.cs", "public class OldFile { }");
        var commitA = repo.CommitAll("initial");

        // Commit B: modify Service.cs and delete OldFile.cs
        repo.WriteFile("Service.cs", "public class Service { int x; }");
        repo.DeleteFile("OldFile.cs");
        var commitB = repo.CommitAll("modify and delete");

        var driver = new GitDiffDriver(repo.Root);
        var result = await driver.GetChangedFilesAsync(commitA, commitB);

        result.Should().Contain(c => c.Kind == FileChangeKind.Modified && c.Path == "Service.cs");
        result.Should().Contain(c => c.Kind == FileChangeKind.Deleted && c.Path == "OldFile.cs");
    }

    /// <summary>
    /// Acceptance scenario: Given a file renamed from Old.cs to New.cs,
    /// When GitDiffDriver.GetChangedFiles is called with rename detection,
    /// Then result contains a rename entry mapping old to new path.
    /// </summary>
    [TestMethod]
    public async Task GetChangedFiles_RenameDetection_OldToNew()
    {
        using var repo = new TempGitRepo();

        // Commit A: create Old.cs
        repo.WriteFile("Old.cs", "public class Old { void M() { } }");
        var commitA = repo.CommitAll("initial");

        // Commit B: rename Old.cs → New.cs (use git mv for rename detection)
        repo.GitMv("Old.cs", "New.cs");
        var commitB = repo.CommitAll("rename");

        var driver = new GitDiffDriver(repo.Root);
        var result = await driver.GetChangedFilesAsync(commitA, commitB);

        var rename = result.FirstOrDefault(c => c.Kind == FileChangeKind.Renamed);
        rename.Should().NotBeNull("a rename entry should be detected");
        rename!.OldPath.Should().Be("Old.cs");
        rename.Path.Should().Be("New.cs");
        rename.SimilarityPercent.Should().BeGreaterThanOrEqualTo(90);
    }

    /// <summary>
    /// End-to-end: binary files (detected via --numstat) are excluded.
    /// </summary>
    [TestMethod]
    public async Task GetChangedFiles_BinaryFilesAreExcluded()
    {
        using var repo = new TempGitRepo();

        // Commit A: create a text file
        repo.WriteFile("Code.cs", "class C { }");
        var commitA = repo.CommitAll("initial");

        // Commit B: add a binary file (random bytes) and modify Code.cs
        repo.WriteFile("Code.cs", "class C { int x; }");
        repo.WriteBinaryFile("image.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 });
        var commitB = repo.CommitAll("add binary");

        var driver = new GitDiffDriver(repo.Root);
        var result = await driver.GetChangedFilesAsync(commitA, commitB);

        result.Should().Contain(c => c.Path == "Code.cs");
        result.Should().NotContain(c => c.Path == "image.png",
            "binary files should be filtered out");
    }

    /// <summary>
    /// End-to-end: submodule changes (gitlink mode 160000) are excluded.
    /// Also verifies that .gitmodules (a side-effect of submodule add) is excluded.
    /// </summary>
    [TestMethod]
    public async Task GetChangedFiles_SubmoduleChangesAreExcluded()
    {
        using var repo = new TempGitRepo();

        // Create a second repo to use as a submodule source
        using var subRepo = new TempGitRepo();
        subRepo.WriteFile("lib.cs", "class Lib { }");
        subRepo.CommitAll("sub init");

        // Commit A: a text file only
        repo.WriteFile("App.cs", "class App { }");
        var commitA = repo.CommitAll("initial");

        // Commit B: add the submodule + modify App.cs
        // Use -c protocol.file.allow=always to permit local file:// clone
        repo.RunGit($"-c protocol.file.allow=always submodule add \"{subRepo.Root}\" vendor/sub");
        repo.WriteFile("App.cs", "class App { int x; }");
        var commitB = repo.CommitAll("add submodule");

        var driver = new GitDiffDriver(repo.Root);
        var result = await driver.GetChangedFilesAsync(commitA, commitB);

        result.Should().Contain(c => c.Path == "App.cs");
        result.Should().NotContain(c => c.Path == "vendor/sub",
            "submodule gitlink entries should be filtered out");
        result.Should().NotContain(c => c.Path == ".gitmodules",
            ".gitmodules (submodule add side-effect) should be filtered out");
    }

    /// <summary>
    /// End-to-end: mixed scenario with adds, modifies, deletes.
    /// </summary>
    [TestMethod]
    public async Task GetChangedFiles_MixedChanges()
    {
        using var repo = new TempGitRepo();

        repo.WriteFile("keep.cs", "class Keep { }");
        repo.WriteFile("remove.cs", "class Remove { }");
        var commitA = repo.CommitAll("initial");

        repo.WriteFile("keep.cs", "class Keep { int x; }");
        repo.DeleteFile("remove.cs");
        repo.WriteFile("added.cs", "class Added { }");
        var commitB = repo.CommitAll("mixed changes");

        var driver = new GitDiffDriver(repo.Root);
        var result = await driver.GetChangedFilesAsync(commitA, commitB);

        result.Should().Contain(c => c.Kind == FileChangeKind.Modified && c.Path == "keep.cs");
        result.Should().Contain(c => c.Kind == FileChangeKind.Deleted && c.Path == "remove.cs");
        result.Should().Contain(c => c.Kind == FileChangeKind.Added && c.Path == "added.cs");
    }

    /// <summary>
    /// End-to-end: GetChangedFiles throws InvalidOperationException on invalid commits.
    /// </summary>
    [TestMethod]
    public async Task GetChangedFiles_InvalidCommit_Throws()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("f.cs", "x");
        repo.CommitAll("init");

        var driver = new GitDiffDriver(repo.Root);

        Func<Task> act = () => driver.GetChangedFilesAsync("nonexistent1234", "HEAD");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ───────────────────────────────────────────────────────────────────
    // NUL-delimited parser tests (-z flag output)
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseNulDelimitedNameStatus_BasicChanges()
    {
        // -z format: STATUS\0path\0STATUS\0path\0
        var output = "A\0NewFile.cs\0M\0Service.cs\0D\0OldFile.cs\0";

        var result = GitDiffDriver.ParseNulDelimitedNameStatus(output);

        result.Should().HaveCount(3);
        result[0].Kind.Should().Be(FileChangeKind.Added);
        result[0].Path.Should().Be("NewFile.cs");
        result[1].Kind.Should().Be(FileChangeKind.Modified);
        result[1].Path.Should().Be("Service.cs");
        result[2].Kind.Should().Be(FileChangeKind.Deleted);
        result[2].Path.Should().Be("OldFile.cs");
    }

    [TestMethod]
    public void ParseNulDelimitedNameStatus_RenameDetection()
    {
        // Rename: R095\0oldpath\0newpath\0
        var output = "R095\0Old.cs\0New.cs\0";

        var result = GitDiffDriver.ParseNulDelimitedNameStatus(output);

        result.Should().HaveCount(1);
        var rename = result[0];
        rename.Kind.Should().Be(FileChangeKind.Renamed);
        rename.OldPath.Should().Be("Old.cs");
        rename.Path.Should().Be("New.cs");
        rename.SimilarityPercent.Should().Be(95);
    }

    [TestMethod]
    public void ParseNulDelimitedNameStatus_PathsWithSpaces()
    {
        // Git would quote these with tab-delimited output, but -z preserves them raw
        var output = "A\0path with spaces/file name.cs\0M\0another path/test file.cs\0";

        var result = GitDiffDriver.ParseNulDelimitedNameStatus(output);

        result.Should().HaveCount(2);
        result[0].Path.Should().Be("path with spaces/file name.cs");
        result[1].Path.Should().Be("another path/test file.cs");
    }

    [TestMethod]
    public void ParseNulDelimitedNameStatus_SkipsBinaryAndSubmodule()
    {
        var output = "A\0image.png\0M\0Code.cs\0M\0vendor/sub\0A\0.gitmodules\0";
        var binaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image.png" };
        var submodules = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vendor/sub" };

        var result = GitDiffDriver.ParseNulDelimitedNameStatus(output, binaries, submodules);

        result.Should().ContainSingle();
        result[0].Path.Should().Be("Code.cs");
    }

    [TestMethod]
    public void ParseNulDelimitedNameStatus_EmptyOutput()
    {
        GitDiffDriver.ParseNulDelimitedNameStatus("").Should().BeEmpty();
        GitDiffDriver.ParseNulDelimitedNameStatus("\0").Should().BeEmpty();
    }

    /// <summary>
    /// End-to-end: files with spaces in their names are handled correctly
    /// thanks to -z NUL-delimited output.
    /// </summary>
    [TestMethod]
    public async Task GetChangedFiles_PathsWithSpecialCharacters()
    {
        using var repo = new TempGitRepo();

        repo.WriteFile("normal.cs", "class N { }");
        var commitA = repo.CommitAll("initial");

        repo.WriteFile("path with spaces/file name.cs", "class S { }");
        repo.WriteFile("normal.cs", "class N { int x; }");
        var commitB = repo.CommitAll("add file with spaces");

        var driver = new GitDiffDriver(repo.Root);
        var result = await driver.GetChangedFilesAsync(commitA, commitB);

        result.Should().Contain(c => c.Path == "normal.cs");
        result.Should().Contain(c => c.Path == "path with spaces/file name.cs");
    }

    // ───────────────────────────────────────────────────────────────────
    // Helper: lightweight disposable temp git repo
    // ───────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        public string Root { get; }

        public TempGitRepo()
        {
            this.Root = Path.Combine(Path.GetTempPath(), "gitdiff_test_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(this.Root);
            this.RunGit("init");
            this.RunGit("config user.email \"test@test.com\"");
            this.RunGit("config user.name \"Test\"");
            this.RunGit("config protocol.file.allow always");
        }

        public void WriteFile(string relativePath, string content)
        {
            var fullPath = Path.Combine(this.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(fullPath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(fullPath, content);
        }

        public void WriteBinaryFile(string relativePath, byte[] content)
        {
            var fullPath = Path.Combine(this.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(fullPath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(fullPath, content);
        }

        public void DeleteFile(string relativePath)
        {
            var fullPath = Path.Combine(this.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            File.Delete(fullPath);
        }

        public void GitMv(string from, string to)
        {
            this.RunGit($"mv \"{from}\" \"{to}\"");
        }

        public string CommitAll(string message)
        {
            this.RunGit("add -A");
            this.RunGit($"commit -m \"{message}\" --allow-empty");
            return this.RunGit("rev-parse HEAD").Trim();
        }

        public string RunGit(string arguments)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = this.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {arguments} failed (exit {process.ExitCode}): {stderr}");
            }

            return stdout;
        }

        public void Dispose()
        {
            try
            {
                // Remove read-only attributes that git sets on .git objects
                foreach (var file in Directory.EnumerateFiles(this.Root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(this.Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in test environment
            }
        }
    }
}