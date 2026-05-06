// -----------------------------------------------------------------------
// <copyright file="QueryPlannerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.Memory.Models;
using Ast.Memory.Retrieval;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class QueryPlannerTests
{
    [TestMethod]
    public void PlanQuery_HybridWithFreeTextOnly_SelectsAllFourModes()
    {
        var request = new RetrievalRequest(
            QueryText: "payment processing");

        var modes = QueryPlanner.PlanQuery(request);

        modes.Should().Contain(RetrievalMode.SemanticSearch);
        modes.Should().Contain(RetrievalMode.StructuralDrill);
        modes.Should().Contain(RetrievalMode.CallTrace);
        modes.Should().Contain(RetrievalMode.ExpertiseRecall);
        modes.Should().HaveCount(4);
    }

    [TestMethod]
    public void PlanQuery_HybridWithSeedNodesOnly_SelectsStructuralDrillAndCallTrace()
    {
        var request = new RetrievalRequest(
            SeedNodeIds: ["/src/Services/PaymentService.cs::PaymentService"]);

        var modes = QueryPlanner.PlanQuery(request);

        modes.Should().Contain(RetrievalMode.StructuralDrill);
        modes.Should().Contain(RetrievalMode.CallTrace);
        modes.Should().NotContain(RetrievalMode.SemanticSearch);
        modes.Should().NotContain(RetrievalMode.ExpertiseRecall);
        modes.Should().HaveCount(2);
    }

    [TestMethod]
    public void PlanQuery_HybridWithBothTextAndSeeds_SelectsAllFourModes()
    {
        var request = new RetrievalRequest(
            QueryText: "payment processing",
            SeedNodeIds: ["/src/Services/PaymentService.cs::PaymentService"]);

        var modes = QueryPlanner.PlanQuery(request);

        modes.Should().HaveCount(4);
        modes.Should().Contain(RetrievalMode.StructuralDrill);
        modes.Should().Contain(RetrievalMode.CallTrace);
        modes.Should().Contain(RetrievalMode.SemanticSearch);
        modes.Should().Contain(RetrievalMode.ExpertiseRecall);
    }

    [TestMethod]
    public void PlanQuery_ExplicitCallTraceMode_SelectsOnlyCallTrace()
    {
        var request = new RetrievalRequest(
            Mode: RetrievalMode.CallTrace,
            SeedNodeIds: ["node-1"]);

        var modes = QueryPlanner.PlanQuery(request);

        modes.Should().HaveCount(1);
        modes.Should().Contain(RetrievalMode.CallTrace);
    }

    [TestMethod]
    public void PlanQuery_ExplicitSemanticSearchMode_SelectsOnlySemanticSearch()
    {
        var request = new RetrievalRequest(
            Mode: RetrievalMode.SemanticSearch,
            QueryText: "find the auth handler");

        var modes = QueryPlanner.PlanQuery(request);

        modes.Should().HaveCount(1);
        modes.Should().Contain(RetrievalMode.SemanticSearch);
    }

    [TestMethod]
    public void PlanQuery_ExplicitStructuralDrillMode_SelectsOnlyStructuralDrill()
    {
        var request = new RetrievalRequest(
            Mode: RetrievalMode.StructuralDrill,
            SeedNodeIds: ["node-1"]);

        var modes = QueryPlanner.PlanQuery(request);

        modes.Should().HaveCount(1);
        modes.Should().Contain(RetrievalMode.StructuralDrill);
    }

    [TestMethod]
    public void PlanQuery_ExplicitExpertiseRecallMode_SelectsOnlyExpertiseRecall()
    {
        var request = new RetrievalRequest(
            Mode: RetrievalMode.ExpertiseRecall,
            QueryText: "common pitfalls");

        var modes = QueryPlanner.PlanQuery(request);

        modes.Should().HaveCount(1);
        modes.Should().Contain(RetrievalMode.ExpertiseRecall);
    }

    [TestMethod]
    public void PlanQuery_HybridWithNoInputs_ReturnsEmptyModes()
    {
        var request = new RetrievalRequest();

        var modes = QueryPlanner.PlanQuery(request);

        modes.Should().BeEmpty();
    }

    [TestMethod]
    public void PlanQuery_HybridWithWhitespaceQueryText_TreatsAsNoText()
    {
        var request = new RetrievalRequest(
            QueryText: "   ");

        var modes = QueryPlanner.PlanQuery(request);

        modes.Should().BeEmpty();
    }

    [TestMethod]
    public void PlanQuery_HybridWithEmptySeedNodesList_TreatsAsNoSeeds()
    {
        var request = new RetrievalRequest(
            QueryText: "payment processing",
            SeedNodeIds: []);

        var modes = QueryPlanner.PlanQuery(request);

        modes.Should().HaveCount(4);
        modes.Should().Contain(RetrievalMode.SemanticSearch);
        modes.Should().Contain(RetrievalMode.StructuralDrill);
        modes.Should().Contain(RetrievalMode.CallTrace);
        modes.Should().Contain(RetrievalMode.ExpertiseRecall);
    }

    [TestMethod]
    public void PlanQuery_ModesReturnedInCanonicalOrder()
    {
        var request = new RetrievalRequest(
            QueryText: "payment",
            SeedNodeIds: ["node-1"]);

        var modes = QueryPlanner.PlanQuery(request);

        modes.Should().ContainInOrder(
            RetrievalMode.StructuralDrill,
            RetrievalMode.CallTrace,
            RetrievalMode.SemanticSearch,
            RetrievalMode.ExpertiseRecall);
    }

    [TestMethod]
    public void PlanQuery_NullRequest_ThrowsArgumentNullException()
    {
        var act = () => QueryPlanner.PlanQuery(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}

[TestClass]
public class QueryPlannerValidationTests
{
    [TestMethod]
    public void Validate_StructuralDrillWithoutSeeds_ReturnsError()
    {
        var request = new RetrievalRequest(
            Mode: RetrievalMode.StructuralDrill,
            QueryText: "something");

        var errors = QueryPlanner.Validate(request);

        errors.Should().ContainSingle()
            .Which.Should().Contain("SeedNodeIds");
    }

    [TestMethod]
    public void Validate_CallTraceWithoutSeeds_ReturnsError()
    {
        var request = new RetrievalRequest(
            Mode: RetrievalMode.CallTrace);

        var errors = QueryPlanner.Validate(request);

        errors.Should().ContainSingle()
            .Which.Should().Contain("SeedNodeIds");
    }

    [TestMethod]
    public void Validate_SemanticSearchWithoutQueryText_ReturnsError()
    {
        var request = new RetrievalRequest(
            Mode: RetrievalMode.SemanticSearch,
            SeedNodeIds: ["node-1"]);

        var errors = QueryPlanner.Validate(request);

        errors.Should().ContainSingle()
            .Which.Should().Contain("QueryText");
    }

    [TestMethod]
    public void Validate_ExpertiseRecallWithNoInputs_ReturnsError()
    {
        var request = new RetrievalRequest(
            Mode: RetrievalMode.ExpertiseRecall);

        var errors = QueryPlanner.Validate(request);

        errors.Should().ContainSingle()
            .Which.Should().Contain("QueryText");
    }

    [TestMethod]
    public void Validate_HybridWithNoInputs_ReturnsError()
    {
        var request = new RetrievalRequest();

        var errors = QueryPlanner.Validate(request);

        errors.Should().ContainSingle()
            .Which.Should().Contain("QueryText");
    }

    [TestMethod]
    public void Validate_ValidHybridRequest_ReturnsNoErrors()
    {
        var request = new RetrievalRequest(
            QueryText: "payment processing");

        var errors = QueryPlanner.Validate(request);

        errors.Should().BeEmpty();
    }

    [TestMethod]
    public void Validate_ValidExplicitMode_ReturnsNoErrors()
    {
        var request = new RetrievalRequest(
            Mode: RetrievalMode.CallTrace,
            SeedNodeIds: ["node-1"]);

        var errors = QueryPlanner.Validate(request);

        errors.Should().BeEmpty();
    }

    [TestMethod]
    public void Validate_NullRequest_ThrowsArgumentNullException()
    {
        var act = () => QueryPlanner.Validate(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
