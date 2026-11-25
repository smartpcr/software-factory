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
/// Phase 108 — the product rule catalog. Tags attach policies automatically;
/// a policy brings its rules; on one work item the operator can attach or
/// detach a policy or a single rule, which wins over the automatic choice.
/// The stage text below is the work item description of harness:DSH-M2 stage
/// 3.2 (2026-11-25), whose agent ran the full harness gate inside its
/// iterations: it never says "full gate", but names tools/harness-gate and
/// "both lanes".
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

    private static readonly RuleSpec LongChecks = new("r-long", "Long-running checks", "Never run the full gate in an iteration.", true);
    private static readonly RuleSpec ScopedVet = new("r-vet", "Scoped vet and test", "Vet and test only the packages you changed.", true);
    private static readonly RuleSpec FakeSpawner = new("r-fake", "Use the fake spawner", "Prefer the fake spawner in unit tests.", true);

    private static readonly PolicySpec HarnessGate = new("p-gate", "Harness gate", new[] { "r-long", "r-vet" }, new[] { "t-gate" }, true);
    private static readonly PolicySpec LabE2e = new("p-lab", "Lab E2E", new[] { "r-long" }, Array.Empty<string>(), true);

    private static RuleSubject Subject(string text = Stage32Text, string repo = "agent-plugin", string category = "impl") =>
        new("ws-3-2", repo, category, text);

    private static WorkItemRules Resolve(
        RuleSubject? subject = null,
        IEnumerable<PolicySpec>? policies = null,
        IEnumerable<RuleSpec>? rules = null,
        Dictionary<string, bool>? tagOverrides = null,
        Dictionary<string, bool>? policyOverrides = null,
        Dictionary<string, bool>? ruleOverrides = null) =>
        RuleCatalogResolver.Resolve(
            subject ?? Subject(), "harness", new[] { FullGate },
            policies ?? new[] { HarnessGate, LabE2e },
            rules ?? new[] { LongChecks, ScopedVet, FakeSpawner },
            tagOverrides, policyOverrides, ruleOverrides);

    [Fact]
    public void Tag_MatchesStageText_AndSaysWhy()
    {
        var m = RuleTagMatcher.Match(FullGate, Subject());

        Assert.True(m.Matched);
        Assert.Contains("\"both lanes\"", m.Reason);
        Assert.Contains("\"tools/harness-gate\"", m.Reason);
    }

    [Fact]
    public void Tag_IsCaseInsensitive_AndLimitedToItsReposAndCategories()
    {
        Assert.True(RuleTagMatcher.Match(FullGate, Subject("Run it on BOTH LANES.")).Matched);
        Assert.Contains("repo forge", RuleTagMatcher.Match(FullGate, Subject(repo: "forge")).Reason);

        var e2eOnly = FullGate with { Categories = new[] { "qa-e2e" } };
        Assert.False(RuleTagMatcher.Match(e2eOnly, Subject()).Matched);
        Assert.True(RuleTagMatcher.Match(e2eOnly, Subject(category: "qa-e2e")).Matched);
    }

    [Fact]
    public void Tag_WithoutPhrasesOrPatterns_AppliesToEveryWorkItemInScope()
    {
        var m = RuleTagMatcher.Match(FullGate with { Phrases = Array.Empty<string>() }, Subject("nothing relevant"));

        Assert.True(m.Matched);
        Assert.Equal("every workstream in agent-plugin", m.Reason);
    }

    [Fact]
    public void Pattern_MatchesQuotesTheHit_AndAnInvalidOneIsReportedAndSkipped()
    {
        var m = RuleTagMatcher.Match(FullGate with { Phrases = Array.Empty<string>(), Patterns = new[] { @"harness-gate/\w+" } }, Subject());
        Assert.True(m.Matched);
        Assert.Contains("harness-gate/analyzers", m.Reason);

        Assert.NotNull(RuleTagMatcher.ValidatePattern("(unclosed"));
        Assert.False(RuleTagMatcher.Match(FullGate with { Phrases = Array.Empty<string>(), Patterns = new[] { "(unclosed" } }, Subject()).Matched);
    }

    [Fact]
    public void TaggedPolicy_BringsAllItsRules_InPolicyOrder()
    {
        var r = Resolve();

        Assert.Equal(new[] { "r-long", "r-vet" }, r.Rules.Select(x => x.Id));
        Assert.All(r.Rules, x => Assert.Equal(new[] { "Harness gate" }, x.Via));
        var gate = r.Policies.Single(p => p.Id == "p-gate");
        Assert.True(gate.Applied);
        Assert.Equal("tag", gate.Source);
        Assert.Equal(new[] { "full harness gate" }, gate.Via);
        Assert.Equal("none", r.Policies.Single(p => p.Id == "p-lab").Source);
    }

    [Fact]
    public void NoTagMatch_NoPolicy_NoRule()
    {
        var r = Resolve(Subject("Add a field to the Spec struct."));

        Assert.Empty(r.Rules);
        Assert.All(r.Policies, p => Assert.False(p.Applied));
    }

    [Fact]
    public void AttachedPolicy_AppliesWithoutATag_AndARuleInTwoPoliciesAppearsOnce()
    {
        var r = Resolve(policyOverrides: new() { ["p-lab"] = true });

        Assert.Equal(new[] { "r-long", "r-vet" }, r.Rules.Select(x => x.Id));
        Assert.Equal(new[] { "Harness gate", "Lab E2E" }, r.Rules.Single(x => x.Id == "r-long").Via);
        Assert.Equal("attached", r.Policies.Single(p => p.Id == "p-lab").Source);
    }

    [Fact]
    public void DetachedPolicy_StaysOut_EvenWhenItsTagApplies()
    {
        var r = Resolve(policyOverrides: new() { ["p-gate"] = false });

        Assert.Empty(r.Rules);
        Assert.Equal("detached", r.Policies.Single(p => p.Id == "p-gate").Source);
    }

    [Fact]
    public void SingleRule_CanBeAttachedAlone_OrKeptOutOfAnAppliedPolicy()
    {
        var r = Resolve(ruleOverrides: new() { ["r-fake"] = true, ["r-vet"] = false });

        Assert.Equal(new[] { "r-long", "r-fake" }, r.Rules.Select(x => x.Id));
        Assert.True(r.Rules.Single(x => x.Id == "r-fake").Attached);
        var vet = r.Choices.Single(c => c.Id == "r-vet");
        Assert.Equal("detached", vet.Source);
        Assert.Equal(new[] { "Harness gate" }, vet.Policies);
    }

    [Fact]
    public void Disabled_PolicyOrRule_ReachesNothing_EvenWhenAttached()
    {
        var r = Resolve(
            policies: new[] { HarnessGate with { Enabled = false } },
            rules: new[] { LongChecks, ScopedVet, FakeSpawner with { Enabled = false } },
            ruleOverrides: new() { ["r-fake"] = true });

        Assert.Empty(r.Rules);
        Assert.Equal("disabled", r.Policies.Single().Source);
        Assert.Equal("disabled", r.Choices.Single(c => c.Id == "r-fake").Source);
    }

    [Fact]
    public void TagOverride_RemovesTheTag_AndWithItThePolicy()
    {
        var r = Resolve(tagOverrides: new() { ["t-gate"] = false });

        Assert.Empty(r.Rules);
        Assert.Equal("operator-removed", Assert.Single(r.Tags).Source);
    }

    [Fact]
    public void PromptBlock_NamesEachRuleAndWhyItApplies()
    {
        var block = RuleCatalogResolver.RenderPromptBlock(Resolve(ruleOverrides: new() { ["r-fake"] = true }));

        Assert.Contains("STANDING RULES", block);
        Assert.Contains("1. Long-running checks  [policy Harness gate (tag full harness gate)]", block);
        Assert.Contains("Never run the full gate in an iteration.", block);
        Assert.Contains("3. Use the fake spawner  [attached by the operator]", block);
        Assert.Equal(string.Empty, RuleCatalogResolver.RenderPromptBlock(null));
        Assert.Equal(string.Empty, RuleCatalogResolver.RenderPromptBlock(Resolve(Subject("unrelated"))));
    }

    [Fact]
    public void Validation_RejectsBadRulesPoliciesAndTags()
    {
        Assert.NotNull(RuleCatalogEndpoints.ValidateTag(new("", null, null, null, null, null)));
        Assert.NotNull(RuleCatalogEndpoints.ValidateTag(new("t", null, null, new() { "docs" }, null, null)));
        Assert.NotNull(RuleCatalogEndpoints.ValidateTag(new("t", null, null, null, null, new() { "(bad" })));
        Assert.Null(RuleCatalogEndpoints.ValidateTag(new("full harness gate", null, new() { "agent-plugin" }, new() { "impl" }, new() { "both lanes" }, new() { "lanes/(win|wsl)" })));

        var policies = new[] { "p-gate" };
        Assert.Null(RuleCatalogEndpoints.ValidateRule(new("title", "text", null, true), policies));
        Assert.Null(RuleCatalogEndpoints.ValidateRule(new("title", "text", new() { "p-gate" }, null), policies));
        Assert.NotNull(RuleCatalogEndpoints.ValidateRule(new("title", "text", new() { "p-missing" }, true), policies));
        Assert.NotNull(RuleCatalogEndpoints.ValidateRule(new("title", " ", null, true), policies));

        var rules = new[] { "r-long" };
        var tags = new[] { "t-gate" };
        Assert.Null(RuleCatalogEndpoints.ValidatePolicy(new("Harness gate", null, new() { "r-long" }, new() { "t-gate" }, true), rules, tags));
        Assert.NotNull(RuleCatalogEndpoints.ValidatePolicy(new(" ", null, null, null, null), rules, tags));
        Assert.NotNull(RuleCatalogEndpoints.ValidatePolicy(new("p", null, new() { "r-missing" }, null, null), rules, tags));
        Assert.NotNull(RuleCatalogEndpoints.ValidatePolicy(new("p", null, null, new() { "t-missing" }, null), rules, tags));
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
        db.CatalogRules.Add(new CatalogRuleRow { ProductId = "harness", Title = "t", Text = "x" });
        db.RulePolicies.Add(new RulePolicyRow { ProductId = "harness", Name = "Harness gate", RuleIdsJson = "[\"r\"]" });
        db.WorkstreamTagOverrides.Add(new WorkstreamTagOverrideRow { WorkstreamId = "ws", TagId = "a", On = true });
        db.WorkItemRules.Add(new WorkItemRuleRow { WorkItemId = "wi", RuleId = "r", On = false });
        db.WorkItemPolicies.Add(new WorkItemPolicyRow { WorkItemId = "wi", PolicyId = "p", On = true });
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.RuleTags.CountAsync());
        Assert.True((await db.RulePolicies.SingleAsync()).Enabled);
        Assert.True((await db.WorkItemPolicies.SingleAsync()).On);
        Assert.False((await db.WorkItemRules.SingleAsync()).On);

        db.RulePolicies.Add(new RulePolicyRow { ProductId = "harness", Name = "Harness gate" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ResolveAsync_ServesWorkstreamsAndStandaloneWorkItems_FromTheProductCatalog()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new ProductDbContext(new DbContextOptionsBuilder<ProductDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        db.Repos.Add(new RepoRow { Id = "agent-plugin", ProductId = "harness" });
        db.Workstreams.Add(new WorkstreamRow { Id = "ws-3-2", RepoId = "agent-plugin", Name = "Launch Plan", Category = "impl" });
        db.WorkItems.Add(new WorkItemRow { Id = "ws-3-2", Title = "Launch Plan Required for Every Command", DescriptionMd = Stage32Text });
        db.WorkItems.Add(new WorkItemRow { Id = "WI-42", RepoId = "agent-plugin", Title = "Fix flaky test", DescriptionMd = "flake", TagsJson = "[\"harness-gate\"]" });
        var tag = new RuleTagRow { ProductId = "harness", Name = "full harness gate", RepoIdsJson = "[\"agent-plugin\"]", PhrasesJson = "[\"both lanes\", \"harness-gate\"]" };
        var rule = new CatalogRuleRow { ProductId = "harness", Title = "Long-running checks", Text = "Never run the full gate." };
        var picked = new CatalogRuleRow { ProductId = "harness", Title = "Picked", Text = "b" };
        db.RuleTags.Add(tag);
        db.CatalogRules.AddRange(rule, picked);
        db.RulePolicies.Add(new RulePolicyRow { ProductId = "harness", Name = "Harness gate", RuleIdsJson = $"[\"{rule.Id}\"]", TagIdsJson = $"[\"{tag.Id}\"]" });
        db.RulePolicies.Add(new RulePolicyRow { ProductId = "other", Name = "Harness gate" });
        db.WorkItemRules.Add(new WorkItemRuleRow { WorkItemId = "WI-42", RuleId = picked.Id, On = true });
        await db.SaveChangesAsync();

        var ws = await RuleCatalogResolver.ResolveAsync(db, "ws-3-2", default);
        Assert.NotNull(ws);
        Assert.Equal("harness", ws!.ProductId);
        Assert.Equal("Long-running checks", Assert.Single(ws.Rules).Title);
        Assert.Single(ws.Policies);

        var item = await RuleCatalogResolver.ResolveAsync(db, "WI-42", default);
        Assert.Equal(new[] { "Long-running checks", "Picked" }, item!.Rules.Select(x => x.Title));
        Assert.True(item.Rules.Single(x => x.Title == "Picked").Attached);

        Assert.Null(await RuleCatalogResolver.ResolveAsync(db, "plan-doc:spawn-x:tech-spec.md", default));
    }
}