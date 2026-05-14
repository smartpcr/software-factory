// -----------------------------------------------------------------------
// <copyright file="Phase60IterNotesArchiveCapTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Phase 60 — iter-notes archive count cap. Bounds per-turn prompt
/// growth across long-running gen/eval loops by limiting the
/// <c>.forge/notes/iter-*.md</c> archive prepend to the most-recent
/// 3 entries before the existing 16 KB byte cap kicks in.
///
/// <para>Without this, a 20-iter plan-doc spawn would prepend all 20
/// archive entries (up to 16 KB) into every generator turn, which
/// compounds with the Copilot SDK's full-history shipping per turn to
/// inflate cumulative input tokens across the session. Combined with
/// the Phase 60 Points floor (3 → 90 turns), the cap helps the agent
/// converge instead of stalling on `generator-max-turns`.</para>
/// </summary>
public sealed class Phase60IterNotesArchiveCapTests : IDisposable
{
    private readonly string workspace;

    public Phase60IterNotesArchiveCapTests()
    {
        this.workspace = Path.Combine(
            Path.GetTempPath(),
            "forge-phase60-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.workspace, recursive: true); }
        catch { /* best effort */ }
    }

    private string ArchiveDir() => Path.Combine(this.workspace, ".forge", "notes");

    private async Task SeedIterNoteAsync(int iter, string body)
    {
        var dir = this.ArchiveDir();
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(
            Path.Combine(dir, $"iter-{iter}.md"),
            body);
    }

    [Fact]
    public async Task ReadPriorIterNotes_FiveEntries_OnlyKeepsMostRecent3()
    {
        for (var i = 1; i <= 5; i++)
        {
            await this.SeedIterNoteAsync(i, $"Iteration {i} content.");
        }

        var result = await CrewStoryArtifactStrategy.ReadPriorIterNotesAsync(this.workspace, default);

        Assert.Contains("### Iteration 5", result);
        Assert.Contains("### Iteration 4", result);
        Assert.Contains("### Iteration 3", result);
        Assert.DoesNotContain("### Iteration 2", result);
        Assert.DoesNotContain("### Iteration 1", result);
    }

    [Fact]
    public async Task ReadPriorIterNotes_FiveEntries_AnnouncesDroppedIters()
    {
        for (var i = 1; i <= 5; i++)
        {
            await this.SeedIterNoteAsync(i, $"Iteration {i} content.");
        }

        var result = await CrewStoryArtifactStrategy.ReadPriorIterNotesAsync(this.workspace, default);

        // Marker must list the dropped iter numbers so the agent can
        // re-derive context from code/PR history if needed.
        Assert.Contains("only the most-recent 3 iteration", result);
        Assert.Contains("2", result); // dropped iter 2
        Assert.Contains("1", result); // dropped iter 1
    }

    [Fact]
    public async Task ReadPriorIterNotes_ExactlyCap_NoMarker()
    {
        for (var i = 1; i <= 3; i++)
        {
            await this.SeedIterNoteAsync(i, $"Iteration {i} content.");
        }

        var result = await CrewStoryArtifactStrategy.ReadPriorIterNotesAsync(this.workspace, default);

        Assert.Contains("### Iteration 1", result);
        Assert.Contains("### Iteration 2", result);
        Assert.Contains("### Iteration 3", result);
        Assert.DoesNotContain("only the most-recent", result);
    }

    [Fact]
    public async Task ReadPriorIterNotes_FewerThanCap_AllIncluded()
    {
        await this.SeedIterNoteAsync(1, "First iter content.");
        await this.SeedIterNoteAsync(2, "Second iter content.");

        var result = await CrewStoryArtifactStrategy.ReadPriorIterNotesAsync(this.workspace, default);

        Assert.Contains("### Iteration 1", result);
        Assert.Contains("### Iteration 2", result);
        Assert.DoesNotContain("only the most-recent", result);
    }

    [Fact]
    public async Task ReadPriorIterNotes_NewestFirstOrdering()
    {
        // Seed out-of-order to verify the descending sort still picks
        // the highest 3 iter numbers regardless of file-system order.
        await this.SeedIterNoteAsync(7, "Iter 7 body.");
        await this.SeedIterNoteAsync(2, "Iter 2 body.");
        await this.SeedIterNoteAsync(10, "Iter 10 body.");
        await this.SeedIterNoteAsync(5, "Iter 5 body.");
        await this.SeedIterNoteAsync(1, "Iter 1 body.");

        var result = await CrewStoryArtifactStrategy.ReadPriorIterNotesAsync(this.workspace, default);

        // Kept: 10, 7, 5. Dropped: 2, 1.
        Assert.Contains("Iter 10 body.", result);
        Assert.Contains("Iter 7 body.", result);
        Assert.Contains("Iter 5 body.", result);
        Assert.DoesNotContain("Iter 2 body.", result);
        Assert.DoesNotContain("Iter 1 body.", result);

        // Newest first -- "10" should appear before "7" in the rendered string.
        var idx10 = result.IndexOf("### Iteration 10", StringComparison.Ordinal);
        var idx7 = result.IndexOf("### Iteration 7", StringComparison.Ordinal);
        var idx5 = result.IndexOf("### Iteration 5", StringComparison.Ordinal);
        Assert.True(idx10 >= 0 && idx7 > idx10 && idx5 > idx7,
            $"Expected newest-first order: idx10={idx10}, idx7={idx7}, idx5={idx5}");
    }

    [Fact]
    public async Task ReadPriorIterNotes_EmptyArchive_LegacyFileFallbackStillWorks()
    {
        // A.1 backward compat: when no archive exists, fall through to
        // the single-file `.forge/iter-notes.md`.
        var forgeDir = Path.Combine(this.workspace, ".forge");
        Directory.CreateDirectory(forgeDir);
        await File.WriteAllTextAsync(
            Path.Combine(forgeDir, "iter-notes.md"),
            "Legacy single-file notes content.");

        var result = await CrewStoryArtifactStrategy.ReadPriorIterNotesAsync(this.workspace, default);

        Assert.Contains("Legacy single-file notes content.", result);
        // Phase 60 marker should NOT appear for the legacy path.
        Assert.DoesNotContain("only the most-recent", result);
    }

    [Fact]
    public async Task ReadPriorIterNotes_NoNotesAtAll_ReturnsEmpty()
    {
        var result = await CrewStoryArtifactStrategy.ReadPriorIterNotesAsync(this.workspace, default);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task ReadPriorIterNotes_EmptyWorkspace_ReturnsEmpty()
    {
        var result = await CrewStoryArtifactStrategy.ReadPriorIterNotesAsync(string.Empty, default);
        Assert.Equal(string.Empty, result);
    }
}
