// -----------------------------------------------------------------------
// <copyright file="RuleCatalogTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Forge.Api.Endpoints;
using Forge.Api.Persistence;
using Forge.Api.Services.Rules;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Phase 108 — the product rule catalog. A tag's matchers decide which
/// workstreams it applies to, an operator override wins over them, and the
/// rules of applied tags reach the workstream's prompt. The stage text below is
/// the work item description of harness:DSH-M2 stage 3.2 (2026-11-25), whose
/// agent ran the full harness gate inside its iterations: it never says "full
/// gate", but names tools/harness-gate and "both lanes".
/// </summary>
public sealed class RuleCatalogTests
{
    private const string Stage32Text =
        "Launch Plan Required for Every Command\n"
        + "## Implementation Steps\n"
        + "- [ ] Extend the `onespawn` analyzer in `tools/harness-gate/analyzers` to also reject a `subprocess.Spec` literal without `Launch` outside tests.\n"
        + "## Test Scenarios\n"
        + "- Scenario: Rejected confirm leaves no command -- [proof: service:os-process; deps: os-process, filesystem on both lanes]\n";

    private static readonly RuleTagSpec FullGate = new(
        "t-gate",
        "full harness gate",
        new[] { "agent-plugin" },
        Array.Empty<string>(),
        new[] { "both lanes", "tools/harness-gate", "lanes/win.ps1" },
        Array.Empty<string>());

    private static RuleSubject Subject(string text = Stage32Text, string repo = "agent-plugin", string category = "impl") =>
        new("ws-3-2", repo, category, text);

    [Fact]
    public void Tag_MatchesStageText_AndSaysWhy()
    {
        var m = RuleTagMatcher.Match(FullGate, Subject());

        Assert.True(m.Matched);
        Assert.Contains("\"both lanes\"", m.Reason);
        Assert.Contains("\"tools/harness-gate\"", m.Reason);
    }

    [Fact]
    public void Tag_IsCaseInsensitive()
    {
        Assert.True(RuleTagMatcher.Match(FullGate, Subject("Run it on BOTH LANES.")).Matched);
    }

    [Fact]
    public void Tag_DoesNotApplyOutsideItsRepos()
    {
        var m = RuleTagMatcher.Match(FullGate, Subject(repo: "forge"));

        Assert.False(m.Matched);
        Assert.Contains("repo forge", m.Reason);
    }

    [Fact]
    public void Tag_DoesNotApplyOutsideItsCategories()
    {
        var e2eOnly = FullGate with { Categories = new[] { "qa-e2e" } };

        Assert.False(RuleTagMatcher.Match(e2eOnly, Subject()).Matched);
        Assert.True(RuleTagMatcher.Match(e2eOnly, Subject(category: "qa-e2e")).Matched);
    }

    [Fact]
    public void Tag_WithoutPhrasesOrPatterns_AppliesToEveryWorkstreamInScope()
    {
        var everyAgentPlugin = FullGate with { Phrases = Array.Empty<string>() };

        var m = RuleTagMatcher.Match(everyAgentPlugin, Subject("nothing relevant"));

        Assert.True(m.Matched);
        Assert.Equal("every workstream in agent-plugin", m.Reason);
    }

    [Fact]
    public void Tag_WithNoHit_DoesNotApply()
    {
        Assert.False(RuleTagMatcher.Match(FullGate, Subject("Add a field to the Spec struct.")).Matched);
    }

    [Fact]
    public void Pattern_MatchesAndQuotesTheHit()
    {
        var tag = FullGate with { Phrases = Array.Empty<string>(), Patterns = new[] { @"harness-gate/\w+" } };

        var m = RuleTagMatcher.Match(tag, Subject());

        Assert.True(m.Matched);
        Assert.Contains("harness-gate/analyzers", m.Reason);
    }

    [Fact]
    public void InvalidPattern_IsReportedOnSave_AndSkippedWhenMatching()
    {
        Assert.NotNull(RuleTagMatcher.ValidatePattern("(unclosed"));
        Assert.Null(RuleTagMatcher.ValidatePattern(@"lanes/(win|wsl)"));

        var tag = FullGate with { Phrases = Array.Empty<string>(), Patterns = new[] { "(unclosed" } };
        Assert.False(RuleTagMatcher.Match(tag, Subject()).Matched);
    }

    [Fact]
    public void Resolve_AttachesTheRulesOfAppliedTags()
    {
        var rules = new[]
        {
            new RuleSpec("r1", "Long-running checks", "Never run the full gate in an iteration.", new[] { "t-gate" }, true),
            new RuleSpec("r2", "Disabled rule", "x", new[] { "t-gate" }, false),
            new RuleSpec("r3", "Other tag", "y", new[] { "t-other" }, true),
        };

        var r = RuleCatalogResolver.Resolve(Subject(), "harness", new[] { FullGate }, new Dictionary<string, bool>(), rules);

        var rule = Assert.Single(r.Rules);
        Assert.Equal("r1", rule.Id);
        Assert.Equal(new[] { "full harness gate" }, rule.Via);
        Assert.Equal("matched", Assert.Single(r.Tags).Source);
    }

    [Fact]
    public void Override_RemovesAMatchedTag_AndItsRules()
    {
        var rules = new[] { new RuleSpec("r1", "Long-running checks", "text", new[] { "t-gate" }, true) };

        var r = RuleCatalogResolver.Resolve(
            Subject(), "harness", new[] { FullGate }, new Dictionary<string, bool> { ["t-gate"] = false }, rules);

        Assert.Empty(r.Rules);
        var tag = Assert.Single(r.Tags);
        Assert.False(tag.Applied);
        Assert.Equal("operator-removed", tag.Source);
    }

    [Fact]
    public void Override_AddsATagNothingMatched()
    {
        var rules = new[] { new RuleSpec("r1", "Long-running checks", "text", new[] { "t-gate" }, true) };

        var r = RuleCatalogResolver.Resolve(
            Subject("unrelated", repo: "forge"), "harness", new[] { FullGate }, new Dictionary<string, bool> { ["t-gate"] = true }, rules);

        Assert.Single(r.Rules);
        Assert.Equal("operator-added", Assert.Single(r.Tags).Source);
    }

    [Fact]
    public void AttachedRule_ReachesTheItem_WithoutAnyTag()
    {
        var rules = new[] { new RuleSpec("r9", "Picked by hand", "Use the fake spawner.", new[] { "t-other" }, true) };

        var r = RuleCatalogResolver.Resolve(
            Subject("unrelated"), "harness", new[] { FullGate }, new Dictionary<string, bool>(), rules,
            new Dictionary<string, bool> { ["r9"] = true });

        var rule = Assert.Single(r.Rules);
        Assert.True(rule.Attached);
        Assert.Equal("attached", Assert.Single(r.Choices).Source);
        Assert.Contains("[attached by the operator]", RuleCatalogResolver.RenderPromptBlock(r));
    }

    [Fact]
    public void DetachedRule_StaysOut_EvenWhenItsTagApplies()
    {
        var rules = new[] { new RuleSpec("r1", "Long-running checks", "text", new[] { "t-gate" }, true) };

        var r = RuleCatalogResolver.Resolve(
            Subject(), "harness", new[] { FullGate }, new Dictionary<string, bool>(), rules,
            new Dictionary<string, bool> { ["r1"] = false });

        Assert.Empty(r.Rules);
        var choice = Assert.Single(r.Choices);
        Assert.Equal("detached", choice.Source);
        Assert.Equal(new[] { "full harness gate" }, choice.Via);
    }

    [Fact]
    public void Choices_ListEveryRule_WithWhyItDoesOrDoesNotApply()
    {
        var rules = new[]
        {
            new RuleSpec("a", "A via tag", "x", new[] { "t-gate" }, true),
            new RuleSpec("b", "B no tag applies", "x", new[] { "t-other" }, true),
            new RuleSpec("c", "C disabled", "x", new[] { "t-gate" }, false),
        };

        var r = RuleCatalogResolver.Resolve(
            Subject(), "harness", new[] { FullGate }, new Dictionary<string, bool>(), rules,
            new Dictionary<string, bool> { ["c"] = true });

        Assert.Equal(new[] { "tag", "none", "disabled" }, r.Choices.Select(c => c.Source));
        Assert.Equal(new[] { "a" }, r.Rules.Select(x => x.Id));
    }

    [Fact]
    public void PromptBlock_NamesEachRuleAndWhyItApplies()
    {
        var rules = new[] { new RuleSpec("r1", "Long-running checks", "Never run the full gate in an iteration.", new[] { "t-gate" }, true) };
        var r = RuleCatalogResolver.Resolve(Subject(), "harness", new[] { FullGate }, new Dictionary<string, bool>(), rules);

        var block = RuleCatalogResolver.RenderPromptBlock(r);

        Assert.Contains("STANDING RULES", block);
        Assert.Contains("1. Long-running checks  [tag full harness gate: matched", block);
        Assert.Contains("Never run the full gate in an iteration.", block);
        Assert.Equal(string.Empty, RuleCatalogResolver.RenderPromptBlock(r with { Rules = Array.Empty<AppliedRule>() }));
        Assert.Equal(string.Empty, RuleCatalogResolver.RenderPromptBlock(null));
    }

    [Fact]
    public void Validation_RejectsBadTagsAndRules()
    {
        Assert.NotNull(RuleCatalogEndpoints.ValidateTag(new("", null, null, null, null, null)));
        Assert.NotNull(RuleCatalogEndpoints.ValidateTag(new("t", null, null, new() { "docs" }, null, null)));
        Assert.NotNull(RuleCatalogEndpoints.ValidateTag(new("t", null, null, null, null, new() { "(bad" })));
        Assert.Null(RuleCatalogEndpoints.ValidateTag(new("full harness gate", null, new() { "agent-plugin" }, new() { "impl" }, new() { "both lanes" }, new() { "lanes/(win|wsl)" })));

        var known = new[] { "t-gate" };
        Assert.Null(RuleCatalogEndpoints.ValidateRule(new("title", "text", new(), true), known));
        Assert.NotNull(RuleCatalogEndpoints.ValidateRule(new("title", "text", new() { "t-missing" }, true), known));
        Assert.NotNull(RuleCatalogEndpoints.ValidateRule(new("title", " ", new() { "t-gate" }, true), known));
        Assert.Null(RuleCatalogEndpoints.ValidateRule(new("title", "text", new() { "t-gate" }, null), known));
    }

    [Fact]
    public async Task Migration108_IsIdempotent_AndMatchesTheEfModel()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var control = new ControlDbContext(new DbContextOptionsBuilder<ControlDbContext>().UseSqlite(conn).Options);
        await using var db = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(conn).Options);

        await Phase108Migration.ApplyAsync(control, db);
        await Phase108Migration.ApplyAsync(control, db);

        db.RuleTags.Add(new RuleTagRow { ProductId = "harness", Name = "full harness gate" });
        db.CatalogRules.Add(new CatalogRuleRow { ProductId = "harness", Title = "t", Text = "x", TagIdsJson = "[\"a\"]" });
        db.WorkstreamTagOverrides.Add(new WorkstreamTagOverrideRow { WorkstreamId = "ws", TagId = "a", On = true });
        db.WorkItemRules.Add(new WorkItemRuleRow { WorkItemId = "wi", RuleId = "r", On = false });
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.RuleTags.CountAsync());
        Assert.True((await db.CatalogRules.SingleAsync()).Enabled);
        Assert.True((await db.WorkstreamTagOverrides.SingleAsync()).On);
        Assert.False((await db.WorkItemRules.SingleAsync()).On);

        db.RuleTags.Add(new RuleTagRow { ProductId = "harness", Name = "full harness gate" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ResolveAsync_ReadsTheWorkstreamItsWorkItemAndTheProductCatalog()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        db.Repos.Add(new RepoRow { Id = "agent-plugin", ProductId = "harness" });
        db.Workstreams.Add(new WorkstreamRow { Id = "ws-3-2", RepoId = "agent-plugin", Name = "Launch Plan", Category = "impl" });
        db.WorkItems.Add(new WorkItemRow { Id = "ws-3-2", Title = "Launch Plan Required for Every Command", DescriptionMd = Stage32Text });
        var tag = new RuleTagRow
        {
            ProductId = "harness",
            Name = "full harness gate",
            RepoIdsJson = "[\"agent-plugin\"]",
            PhrasesJson = "[\"both lanes\"]",
        };
        db.RuleTags.Add(tag);
        db.RuleTags.Add(new RuleTagRow { ProductId = "other", Name = "full harness gate" });
        db.CatalogRules.Add(new CatalogRuleRow
        {
            ProductId = "harness",
            Title = "Long-running checks",
            Text = "Never run the full gate in an iteration.",
            TagIdsJson = $"[\"{tag.Id}\"]",
        });
        await db.SaveChangesAsync();

        var r = await RuleCatalogResolver.ResolveAsync(db, "ws-3-2", default);

        Assert.NotNull(r);
        Assert.Equal("harness", r!.ProductId);
        Assert.Equal("Long-running checks", Assert.Single(r.Rules).Title);
        Assert.Single(r.Tags);
        Assert.Null(await RuleCatalogResolver.ResolveAsync(db, "plan-doc:spawn-x:tech-spec.md", default));
    }

    [Fact]
    public async Task ResolveAsync_ServesAStandaloneWorkItem_MatchingItsTagsAndAttachedRules()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        db.Repos.Add(new RepoRow { Id = "agent-plugin", ProductId = "harness" });
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "WI-42",
            RepoId = "agent-plugin",
            Title = "Fix flaky budget store test",
            DescriptionMd = "TestBudgetStoreAppend fails on Windows.",
            TagsJson = "[\"harness-gate\"]",
        });
        var tag = new RuleTagRow { ProductId = "harness", Name = "gate work", PhrasesJson = "[\"harness-gate\"]" };
        db.RuleTags.Add(tag);
        var viaTag = new CatalogRuleRow { ProductId = "harness", Title = "Via tag", Text = "a", TagIdsJson = $"[\"{tag.Id}\"]" };
        var picked = new CatalogRuleRow { ProductId = "harness", Title = "Picked", Text = "b", TagIdsJson = "[\"none\"]" };
        db.CatalogRules.AddRange(viaTag, picked);
        db.WorkItemRules.Add(new WorkItemRuleRow { WorkItemId = "WI-42", RuleId = picked.Id, On = true });
        await db.SaveChangesAsync();

        var r = await RuleCatalogResolver.ResolveAsync(db, "WI-42", default);

        Assert.NotNull(r);
        Assert.Equal(new[] { "Picked", "Via tag" }, r!.Rules.Select(x => x.Title));
        Assert.True(r.Rules.Single(x => x.Title == "Picked").Attached);
        Assert.Equal("matched", Assert.Single(r.Tags).Source);
    }
}