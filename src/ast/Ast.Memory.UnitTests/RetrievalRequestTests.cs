// -----------------------------------------------------------------------
// <copyright file="RetrievalRequestTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.Memory.Models;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class RetrievalRequestTests
{
    [TestMethod]
    public void TokenBudget_DefaultValue_Is8000()
    {
        var request = new RetrievalRequest();

        request.TokenBudget.Should().Be(8_000);
    }

    [TestMethod]
    public void TokenBudget_ExceedsCeiling_ClampedTo32000()
    {
        var request = new RetrievalRequest(TokenBudget: 99_999);

        request.TokenBudget.Should().Be(RetrievalRequest.TokenBudgetCeiling);
    }

    [TestMethod]
    public void TokenBudget_Zero_ClampedToFloor()
    {
        var request = new RetrievalRequest(TokenBudget: 0);

        request.TokenBudget.Should().Be(RetrievalRequest.TokenBudgetFloor);
    }

    [TestMethod]
    public void TokenBudget_Negative_ClampedToFloor()
    {
        var request = new RetrievalRequest(TokenBudget: -5);

        request.TokenBudget.Should().Be(RetrievalRequest.TokenBudgetFloor);
    }

    [TestMethod]
    public void TokenBudget_WithinRange_PreservedExactly()
    {
        var request = new RetrievalRequest(TokenBudget: 16_000);

        request.TokenBudget.Should().Be(16_000);
    }

    [TestMethod]
    public void TokenBudget_AtCeiling_Preserved()
    {
        var request = new RetrievalRequest(TokenBudget: 32_000);

        request.TokenBudget.Should().Be(32_000);
    }

    [TestMethod]
    public void TokenBudget_AtFloor_Preserved()
    {
        var request = new RetrievalRequest(TokenBudget: 1);

        request.TokenBudget.Should().Be(1);
    }

    [TestMethod]
    public void Mode_DefaultsToHybrid()
    {
        var request = new RetrievalRequest();

        request.Mode.Should().Be(RetrievalMode.Hybrid);
    }

    [TestMethod]
    public void HasQueryText_NullQueryText_ReturnsFalse()
    {
        var request = new RetrievalRequest(QueryText: null);

        request.HasQueryText.Should().BeFalse();
    }

    [TestMethod]
    public void HasQueryText_EmptyString_ReturnsFalse()
    {
        var request = new RetrievalRequest(QueryText: "");

        request.HasQueryText.Should().BeFalse();
    }

    [TestMethod]
    public void HasQueryText_WhitespaceOnly_ReturnsFalse()
    {
        var request = new RetrievalRequest(QueryText: "   ");

        request.HasQueryText.Should().BeFalse();
    }

    [TestMethod]
    public void HasQueryText_ValidText_ReturnsTrue()
    {
        var request = new RetrievalRequest(QueryText: "payment processing");

        request.HasQueryText.Should().BeTrue();
    }

    [TestMethod]
    public void HasSeedNodes_Null_ReturnsFalse()
    {
        var request = new RetrievalRequest(SeedNodeIds: null);

        request.HasSeedNodes.Should().BeFalse();
    }

    [TestMethod]
    public void HasSeedNodes_EmptyList_ReturnsFalse()
    {
        var request = new RetrievalRequest(SeedNodeIds: []);

        request.HasSeedNodes.Should().BeFalse();
    }

    [TestMethod]
    public void HasSeedNodes_WithEntries_ReturnsTrue()
    {
        var request = new RetrievalRequest(SeedNodeIds: ["node-1"]);

        request.HasSeedNodes.Should().BeTrue();
    }

    [TestMethod]
    public void AtCommitIndex_DefaultsToNull()
    {
        var request = new RetrievalRequest();

        request.AtCommitIndex.Should().BeNull();
    }

    [TestMethod]
    public void Weights_DefaultsToNull()
    {
        var request = new RetrievalRequest();

        request.Weights.Should().BeNull();
    }
}
