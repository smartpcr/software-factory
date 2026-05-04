// -----------------------------------------------------------------------
// <copyright file="ExpertiseEntryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using System.Security.Cryptography;
using System.Text;
using Ast.Memory.Models;

[TestClass]
public class ExpertiseEntryTests
{
    [TestMethod]
    public void ExpertiseEntry_IdGeneration_MatchesSpecFormat()
    {
        // Arrange: per test scenario specification
        var title = "Fragile null-handling";
        var body = "processRefund assumes non-null";
        var targetNodeId = "/src/Services/PaymentService.cs::PaymentService.processRefund";

        // Compute expected hash: SHA-256(Title+Body+TargetNodeId), first 8 hex chars
        var expectedHash8 = ComputeHash8(title + body + targetNodeId);
        var expectedId = $"expertise:pitfall:{expectedHash8}";

        // Act
        var entry = new ExpertiseEntry(ExpertiseKind.Pitfall, title, body, targetNodeId, "test-agent");

        // Assert
        entry.Id.Should().Be(expectedId);
        entry.Kind.Should().Be(ExpertiseKind.Pitfall);
        entry.Title.Should().Be(title);
        entry.Body.Should().Be(body);
        entry.TargetNodeId.Should().Be(targetNodeId);
    }

    [TestMethod]
    public void ExpertiseEntry_ContentHash_IsSha256OfTitlePlusBody()
    {
        var title = "Fragile null-handling";
        var body = "processRefund assumes non-null";
        var targetNodeId = "/src/Services/PaymentService.cs::PaymentService.processRefund";

        var expectedContentHash = ComputeFullHash(title + body);

        var entry = new ExpertiseEntry(ExpertiseKind.Pitfall, title, body, targetNodeId, "test-agent");
        entry.ContentHash.Should().Be(expectedContentHash);
    }

    [TestMethod]
    public void ExpertiseEntry_Defaults_AreCorrect()
    {
        var entry = new ExpertiseEntry(ExpertiseKind.Pattern, "Title", "Body", "target", "agent-1");

        entry.Scope.Should().Be("exact");
        entry.Confidence.Should().Be(1.0);
        entry.Archived.Should().BeFalse();
        entry.SourceAgent.Should().Be("agent-1");
        entry.Evidence.Should().BeEmpty();
        entry.Embedding.Should().BeNull();
    }

    [TestMethod]
    public void ExpertiseEntry_ScopeExact_IsValid()
    {
        var entry = new ExpertiseEntry(ExpertiseKind.Insight, "T", "B", "n", "a") { Scope = "exact" };
        entry.Scope.Should().Be("exact");
    }

    [TestMethod]
    public void ExpertiseEntry_ScopeSubtree_IsValid()
    {
        var entry = new ExpertiseEntry(ExpertiseKind.Insight, "T", "B", "n", "a") { Scope = "subtree" };
        entry.Scope.Should().Be("subtree");
    }

    [TestMethod]
    public void ExpertiseEntry_InvalidScope_Throws()
    {
        var act = () => new ExpertiseEntry(ExpertiseKind.Insight, "T", "B", "n", "a") { Scope = "invalid" };
        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("exact");
    }

    [TestMethod]
    public void ExpertiseEntry_ConfidenceBelowZero_Throws()
    {
        var act = () => new ExpertiseEntry(ExpertiseKind.Insight, "T", "B", "n", "a") { Confidence = -0.1 };
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void ExpertiseEntry_ConfidenceAboveOne_Throws()
    {
        var act = () => new ExpertiseEntry(ExpertiseKind.Insight, "T", "B", "n", "a") { Confidence = 1.1 };
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void ExpertiseEntry_ConfidenceBoundary_ZeroAndOne_AreValid()
    {
        var entryZero = new ExpertiseEntry(ExpertiseKind.Insight, "T", "B", "n", "a") { Confidence = 0.0 };
        entryZero.Confidence.Should().Be(0.0);

        var entryOne = new ExpertiseEntry(ExpertiseKind.Insight, "T", "B", "n", "a") { Confidence = 1.0 };
        entryOne.Confidence.Should().Be(1.0);
    }

    [TestMethod]
    public void ExpertiseEntry_EmptyTitle_Throws()
    {
        var act = () => new ExpertiseEntry(ExpertiseKind.Pattern, "", "Body", "target", "a");
        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void ExpertiseEntry_EmptyBody_Throws()
    {
        var act = () => new ExpertiseEntry(ExpertiseKind.Pattern, "Title", "", "target", "a");
        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void ExpertiseEntry_EmptyTargetNodeId_Throws()
    {
        var act = () => new ExpertiseEntry(ExpertiseKind.Pattern, "Title", "Body", "", "a");
        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void ExpertiseEntry_EmptySourceAgent_Throws()
    {
        var act = () => new ExpertiseEntry(ExpertiseKind.Pattern, "Title", "Body", "target", "");
        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void ExpertiseEntry_SourceAgent_IsNonNullable()
    {
        var entry = new ExpertiseEntry(ExpertiseKind.Pattern, "T", "B", "n", "copilot-agent");
        entry.SourceAgent.Should().Be("copilot-agent");
    }

    [TestMethod]
    public void ExpertiseEntry_WithEvidence_RetainsAllCitations()
    {
        var citations = new List<Citation>
        {
            new("node-1", "abc123", "snippet 1"),
            new("node-2", null, null),
        };

        var entry = new ExpertiseEntry(ExpertiseKind.Preference, "T", "B", "n", "a")
        {
            Evidence = citations,
        };

        entry.Evidence.Should().HaveCount(2);
        entry.Evidence[0].NodeId.Should().Be("node-1");
        entry.Evidence[0].CommitSha.Should().Be("abc123");
        entry.Evidence[0].Snippet.Should().Be("snippet 1");
        entry.Evidence[1].CommitSha.Should().BeNull();
    }

    [TestMethod]
    public void ExpertiseEntry_DifferentKinds_ProduceCorrectIdPrefix()
    {
        var pattern = new ExpertiseEntry(ExpertiseKind.Pattern, "T", "B", "n", "a");
        pattern.Id.Should().StartWith("expertise:pattern:");

        var pitfall = new ExpertiseEntry(ExpertiseKind.Pitfall, "T", "B", "n", "a");
        pitfall.Id.Should().StartWith("expertise:pitfall:");

        var preference = new ExpertiseEntry(ExpertiseKind.Preference, "T", "B", "n", "a");
        preference.Id.Should().StartWith("expertise:preference:");

        var insight = new ExpertiseEntry(ExpertiseKind.Insight, "T", "B", "n", "a");
        insight.Id.Should().StartWith("expertise:insight:");
    }

    private static string ComputeHash8(string input)
    {
        // Uses the same concatenation approach as ExpertiseEntry
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static string ComputeFullHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
