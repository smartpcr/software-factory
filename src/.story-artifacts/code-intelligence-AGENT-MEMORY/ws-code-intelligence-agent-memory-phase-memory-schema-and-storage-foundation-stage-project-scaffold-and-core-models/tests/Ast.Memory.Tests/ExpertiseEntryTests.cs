using System.Security.Cryptography;
using System.Text;
using Ast.Memory.Models;
using Xunit;

namespace Ast.Memory.Tests;

public class ExpertiseEntryTests
{
    [Fact]
    public void Id_MatchesDeterministicFormat()
    {
        var entry = new ExpertiseEntry(
            ExpertiseKind.Pitfall,
            "Fragile null-handling",
            "processRefund assumes non-null",
            "/src/Services/PaymentService.cs::PaymentService.processRefund");

        // ID format: expertise:{kind}:{first 8 chars of SHA-256(Title+Body+TargetNodeId)}
        var input = "Fragile null-handling" + "processRefund assumes non-null"
                    + "/src/Services/PaymentService.cs::PaymentService.processRefund";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var expectedHash8 = Convert.ToHexString(hash)[..8].ToLowerInvariant();
        var expectedId = $"expertise:pitfall:{expectedHash8}";

        Assert.Equal(expectedId, entry.Id);
    }

    [Fact]
    public void Id_DiffersByKind()
    {
        var pitfall = new ExpertiseEntry(ExpertiseKind.Pitfall, "Title", "Body", "node1");
        var pattern = new ExpertiseEntry(ExpertiseKind.Pattern, "Title", "Body", "node1");

        Assert.NotEqual(pitfall.Id, pattern.Id);
        Assert.StartsWith("expertise:pitfall:", pitfall.Id);
        Assert.StartsWith("expertise:pattern:", pattern.Id);
    }

    [Fact]
    public void Id_DiffersByTargetNodeId()
    {
        var a = new ExpertiseEntry(ExpertiseKind.Insight, "Title", "Body", "node-a");
        var b = new ExpertiseEntry(ExpertiseKind.Insight, "Title", "Body", "node-b");

        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void ContentHash_IsSha256OfTitlePlusBody()
    {
        var entry = new ExpertiseEntry(ExpertiseKind.Pattern, "Title", "Body", "node1");

        var expected = SHA256.HashData(Encoding.UTF8.GetBytes("TitleBody"));
        var expectedHex = Convert.ToHexString(expected).ToLowerInvariant();

        Assert.Equal(expectedHex, entry.ContentHash);
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var entry = new ExpertiseEntry(ExpertiseKind.Preference, "T", "B", "n");

        Assert.Equal("exact", entry.Scope);
        Assert.Equal(1.0, entry.Confidence);
        Assert.False(entry.Archived);
        Assert.Null(entry.SourceAgent);
        Assert.Empty(entry.Evidence);
        Assert.Null(entry.Embedding);
    }

    [Fact]
    public void Scope_CanBeSetToSubtree()
    {
        var entry = new ExpertiseEntry(ExpertiseKind.Pattern, "T", "B", "n") { Scope = "subtree" };

        Assert.Equal("subtree", entry.Scope);
    }

    [Fact]
    public void Scope_ThrowsOnInvalidValue()
    {
        Assert.Throws<ArgumentException>(() =>
            new ExpertiseEntry(ExpertiseKind.Pattern, "T", "B", "n") { Scope = "invalid" });
    }

    [Fact]
    public void Confidence_CanBeSetToValidValue()
    {
        var entry = new ExpertiseEntry(ExpertiseKind.Pattern, "T", "B", "n") { Confidence = 0.75 };

        Assert.Equal(0.75, entry.Confidence);
    }

    [Fact]
    public void Confidence_ThrowsOnBelowZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExpertiseEntry(ExpertiseKind.Pattern, "T", "B", "n") { Confidence = -0.1 });
    }

    [Fact]
    public void Confidence_ThrowsOnAboveOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExpertiseEntry(ExpertiseKind.Pattern, "T", "B", "n") { Confidence = 1.1 });
    }

    [Fact]
    public void Constructor_ThrowsOnNullTitle()
    {
        Assert.Throws<ArgumentException>(() =>
            new ExpertiseEntry(ExpertiseKind.Pattern, null!, "Body", "node"));
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyBody()
    {
        Assert.Throws<ArgumentException>(() =>
            new ExpertiseEntry(ExpertiseKind.Pattern, "Title", "", "node"));
    }

    [Fact]
    public void Constructor_ThrowsOnNullTargetNodeId()
    {
        Assert.Throws<ArgumentException>(() =>
            new ExpertiseEntry(ExpertiseKind.Pattern, "Title", "Body", null!));
    }

    [Fact]
    public void Evidence_CanBeSetWithCitations()
    {
        var citations = new[]
        {
            new Citation("node1", "abc123", "snippet1"),
            new Citation("node2", null, null)
        };

        var entry = new ExpertiseEntry(ExpertiseKind.Insight, "T", "B", "n")
        {
            Evidence = citations
        };

        Assert.Equal(2, entry.Evidence.Count);
        Assert.Equal("node1", entry.Evidence[0].NodeId);
        Assert.Equal("abc123", entry.Evidence[0].CommitSha);
        Assert.Null(entry.Evidence[1].CommitSha);
    }

    [Fact]
    public void InitProperties_CanBeSet()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new ExpertiseEntry(ExpertiseKind.Pattern, "T", "B", "n")
        {
            Archived = true,
            SourceAgent = "agent-1",
            Embedding = new float[] { 0.1f, 0.2f, 0.3f },
            CreatedAt = now,
            UpdatedAt = now
        };

        Assert.True(entry.Archived);
        Assert.Equal("agent-1", entry.SourceAgent);
        Assert.Equal(3, entry.Embedding!.Length);
        Assert.Equal(now, entry.CreatedAt);
    }
}
