// -----------------------------------------------------------------------
// <copyright file="Phase24CommentResponseLoopTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.IO;
using System.Threading.Tasks;
using Forge.Api.Services;
using Xunit;

/// <summary>
/// Phase 24.17 — comment-response runs through DeliverableLoop with the
/// iterate-to-90 contract (Q24.12). The loop is the same orchestrator as
/// implementation; only the evaluator preamble + Kind discriminator differ.
/// </summary>
public class Phase24CommentResponseLoopTests
{
    [Fact]
    public void DeliverableKinds_Constants_Are_Locked()
    {
        // Q24.12 — the kind discriminator carries through to the loop
        // request; downstream code branches on these literal values.
        Assert.Equal("coding", DeliverableKinds.Coding);
        Assert.Equal("comment-response", DeliverableKinds.CommentResponse);
        Assert.Equal("build-fix", DeliverableKinds.BuildFix);
        Assert.Equal("plan", DeliverableKinds.Plan);
    }

    [Fact]
    public void DeliverableLoopRequest_Defaults_Kind_To_Coding()
    {
        // Q24.12 — back-compat with the pre-Phase-24 loop usage. Existing
        // callers that don't supply Kind get the implementation rubric.
        var req = new DeliverableLoopRequest(
            WorkItemId: "wi-1",
            CrewId: "c-1",
            GeneratorAgentId: "G",
            EvaluatorAgentId: "E",
            Generator: (i, n, ct) => Task.FromResult(new GeneratorRunOutput("ok", 0m)),
            Evaluator: (i, o, ct) => Task.FromResult(new EvaluatorRunOutput("{\"score\":95,\"notes\":\"\"}", 0m)));
        Assert.Equal(DeliverableKinds.Coding, req.Kind);
    }

    [Fact]
    public void DeliverableLoopRequest_Kind_Round_Trips()
    {
        // Q24.12 — the loop carries the kind verbatim so downstream consumers
        // (cost ledger tagging, transcript events) can branch.
        var req = new DeliverableLoopRequest(
            WorkItemId: "wi-1",
            CrewId: "c-1",
            GeneratorAgentId: "G",
            EvaluatorAgentId: "E",
            Generator: (i, n, ct) => Task.FromResult(new GeneratorRunOutput("ok", 0m)),
            Evaluator: (i, o, ct) => Task.FromResult(new EvaluatorRunOutput("{\"score\":95,\"notes\":\"\"}", 0m)),
            Kind: DeliverableKinds.CommentResponse);
        Assert.Equal(DeliverableKinds.CommentResponse, req.Kind);
    }

    [Fact]
    public async Task AgentTemplateLoader_Loads_CommentResponse_Template_From_Build_Output()
    {
        // Q24.13 — the comment-response system template ships in the build
        // output via csproj <Content Include="..\..\..\agents\templates\*.md">.
        // The loader resolves it from AppContext.BaseDirectory/agents/templates
        // first, then walks up to the repo root as a dev-mode fallback.
        AgentTemplateLoader.ClearCache();
        var content = await AgentTemplateLoader.LoadAsync(AgentTemplateLoader.CommentResponseFile);
        Assert.False(string.IsNullOrEmpty(content),
            "comment-response-system.md must be deployed alongside Forge.Api or reachable from the repo root.");
    }

    [Fact]
    public async Task CommentResponse_Template_Carries_Verbatim_MustReply_Rule()
    {
        // Q24.13 — the verbatim language anchors the rubric. If this assertion
        // fails, the spec ↔ template parity is broken; update the template file.
        AgentTemplateLoader.ClearCache();
        var content = await AgentTemplateLoader.LoadAsync(AgentTemplateLoader.CommentResponseFile);

        Assert.Contains("must take exactly one of two actions", content);
        Assert.Contains("Silently dropping a comment is a graded failure", content);
        Assert.Contains("Addresses comment <id>", content);
    }

    [Fact]
    public async Task CommentResponse_Template_Specifies_Scope_Is_Union_Of_Comments()
    {
        // Q24.12 — the rubric note about scope semantics so the evaluator
        // doesn't penalize a small focused reply for "incomplete work".
        AgentTemplateLoader.ClearCache();
        var content = await AgentTemplateLoader.LoadAsync(AgentTemplateLoader.CommentResponseFile);

        Assert.Contains("scope** is the union of the input comments", content);
    }

    [Fact]
    public async Task CommentResponse_Template_Forbids_Auto_Resolve()
    {
        // Q24.5 / Q24.12 — agents do not resolve their own homework; only
        // humans do. The template makes this explicit. The match is robust
        // against markdown line-wrapping by anchoring on the contiguous
        // "auto-resolve threads" phrase.
        AgentTemplateLoader.ClearCache();
        var content = await AgentTemplateLoader.LoadAsync(AgentTemplateLoader.CommentResponseFile);

        Assert.Contains("auto-resolve threads", content, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommentResponse_Template_Path_Resolves_Or_Returns_Null()
    {
        // Negative-path coverage: a missing file name returns null gracefully
        // instead of throwing.
        var path = AgentTemplateLoader.ResolvePath("does-not-exist.md");
        Assert.Null(path);
    }
}
