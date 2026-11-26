// -----------------------------------------------------------------------
// <copyright file="RuleCatalogEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Rules;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 108 — the product rule catalog behind the Policies page. A rule is a
/// standing directive for the agent; a policy groups rules (a rule can be in
/// several); a tag attaches policies (and, since Phase 109, single rules)
/// automatically to the work items whose repo and text it matches. Phase 109
/// also applies a policy to a whole product or story. On one work item the
/// operator can attach or detach a policy, a single rule, or a tag, which wins
/// over the automatic choice. Reads are open to product readers; writes need a
/// paired operator.
/// </summary>
public static class RuleCatalogEndpoints
{
    private const int MaxNameLength = 80;
    private const int MaxTitleLength = 120;
    private const int MaxTextLength = 8000;
    private const int MaxListItems = 50;
    private const int MaxTargets = 50;

    public static IEndpointRouteBuilder MapRuleCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/products/{productId}/rule-catalog");
        grp.MapGet(string.Empty, GetCatalog).WithName("GetRuleCatalog");
        grp.MapPost("/rules", AddRule).WithName("AddCatalogRule");
        grp.MapPut("/rules/{ruleId}", UpdateRule).WithName("UpdateCatalogRule");
        grp.MapDelete("/rules/{ruleId}", DeleteRule).WithName("DeleteCatalogRule");
        grp.MapPost("/policies", AddPolicy).WithName("AddRulePolicy");
        grp.MapPut("/policies/{policyId}", UpdatePolicy).WithName("UpdateRulePolicy");
        grp.MapDelete("/policies/{policyId}", DeletePolicy).WithName("DeleteRulePolicy");
        grp.MapGet("/policies/{policyId}/assignments", PolicyAssignments).WithName("GetRulePolicyAssignments");
        grp.MapPut("/policies/{policyId}/scopes/{kind}/{scopeId}", SetPolicyScope).WithName("SetRulePolicyScope");
        grp.MapDelete("/policies/{policyId}/scopes/{kind}/{scopeId}", ClearPolicyScope).WithName("ClearRulePolicyScope");
        grp.MapPost("/tags", AddTag).WithName("AddRuleTag");
        grp.MapPut("/tags/{tagId}", UpdateTag).WithName("UpdateRuleTag");
        grp.MapDelete("/tags/{tagId}", DeleteTag).WithName("DeleteRuleTag");
        grp.MapGet("/tags/{tagId}/matches", TagMatches).WithName("ListRuleTagMatches");
        grp.MapGet("/targets", Targets).WithName("ListRuleTargets");
        grp.MapGet("/reach", Reach).WithName("GetRuleReach");

        // A workstream's id is its work item id, so one set of routes serves
        // work item executions and story workstreams alike.
        grp.MapGet("/work-items/{workItemId}", WorkItemRulesFor).WithName("GetWorkItemRules");
        grp.MapPut("/work-items/{workItemId}/{kind}/{id}", SetWorkItemChoice).WithName("SetWorkItemChoice");
        grp.MapDelete("/work-items/{workItemId}/{kind}/{id}", ClearWorkItemChoice).WithName("ClearWorkItemChoice");
        return app;
    }

    public sealed record TagRequest(
        string? Name,
        string? Description,
        List<string>? RepoIds,
        List<string>? Categories,
        List<string>? Phrases,
        List<string>? Patterns);

    /// <summary>
    /// A rule; <see cref="PolicyIds"/>, when given, sets exactly which policies
    /// contain it, and <see cref="TagIds"/>, when given, exactly its own tags.
    /// </summary>
    public sealed record RuleRequest(string? Title, string? Text, List<string>? PolicyIds, bool? Enabled, List<string>? TagIds = null);

    public sealed record PolicyRequest(string? Name, string? Description, List<string>? RuleIds, List<string>? TagIds, bool? Enabled);

    public sealed record OnRequest(bool On);

    public sealed record TagDto(
        string Id, string Name, string Description,
        IReadOnlyList<string> RepoIds, IReadOnlyList<string> Categories, IReadOnlyList<string> Phrases, IReadOnlyList<string> Patterns,
        DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset UpdatedAt);

    public sealed record RuleDto(
        string Id, string Title, string Text, bool Enabled, IReadOnlyList<string> TagIds,
        DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset UpdatedAt);

    public sealed record PolicyDto(
        string Id, string Name, string Description, IReadOnlyList<string> RuleIds, IReadOnlyList<string> TagIds, bool Enabled,
        DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset UpdatedAt);

    /// <summary>How many work items the operator attached something to, or detached it from.</summary>
    public sealed record UsageDto(int Attached, int Detached);

    /// <summary>A whole scope a policy is applied to: <c>product</c> or <c>story</c>, with its label.</summary>
    public sealed record ScopeDto(string Kind, string ScopeId, string Label);

    public sealed record CatalogDto(
        string ProductId,
        IReadOnlyList<PolicyDto> Policies,
        IReadOnlyList<RuleDto> Rules,
        IReadOnlyList<TagDto> Tags,
        IReadOnlyDictionary<string, UsageDto> RuleUsage,
        IReadOnlyDictionary<string, UsageDto> PolicyUsage,
        IReadOnlyDictionary<string, IReadOnlyList<ScopeDto>> PolicyScopes);

    /// <summary>A story the operator can apply a policy to, or filter by.</summary>
    public sealed record StoryTargetDto(string SpawnId, string StoryId, string Title, string Phase, int Workstreams);

    /// <summary>A workstream or standalone work item the operator can apply a policy to.</summary>
    public sealed record WorkItemTargetDto(string Id, string Title, string Kind, string? StoryId, string Status);

    public sealed record TargetsDto(IReadOnlyList<StoryTargetDto> Stories, IReadOnlyList<WorkItemTargetDto> WorkItems);

    /// <summary>A work item the operator attached the policy to (<c>On</c>) or removed it from.</summary>
    public sealed record AssignedWorkItemDto(string Id, string Title, string Kind, string? StoryId, bool On);

    /// <summary>Where a policy is applied by hand: whole scopes, and single work items.</summary>
    public sealed record AssignmentsDto(string PolicyId, IReadOnlyList<ScopeDto> Scopes, IReadOnlyList<AssignedWorkItemDto> WorkItems);

    /// <summary>How many of the stories' workstreams each policy and rule reaches.</summary>
    public sealed record ReachDto(int Workstreams, IReadOnlyDictionary<string, int> Policies, IReadOnlyDictionary<string, int> Rules);

    public sealed record TagMatchDto(string WorkstreamId, string Name, string Category, string Status, bool Applied, string Source, string Reason);

    private static TagDto ToDto(RuleTagRow r) => new(
        r.Id, r.Name, r.Description,
        RuleCatalogJson.ReadList(r.RepoIdsJson), RuleCatalogJson.ReadList(r.CategoriesJson),
        RuleCatalogJson.ReadList(r.PhrasesJson), RuleCatalogJson.ReadList(r.PatternsJson),
        r.CreatedAt, r.CreatedBy, r.UpdatedAt);

    private static RuleDto ToDto(CatalogRuleRow r) =>
        new(r.Id, r.Title, r.Text, r.Enabled, RuleCatalogJson.ReadList(r.TagIdsJson), r.CreatedAt, r.CreatedBy, r.UpdatedAt);

    private static PolicyDto ToDto(RulePolicyRow p) => new(
        p.Id, p.Name, p.Description, RuleCatalogJson.ReadList(p.RuleIdsJson), RuleCatalogJson.ReadList(p.TagIdsJson), p.Enabled,
        p.CreatedAt, p.CreatedBy, p.UpdatedAt);

    private static async Task<(ProductRow? Product, ProductDbContext? Db)> ResolveProductAsync(
        string productId, ControlDbContext control, IProductDbContextFactory factory)
    {
        var product = await control.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId || p.Slug == productId);
        return product is null ? (null, null) : (product, factory.CreateFor(product.Slug));
    }

    private static IResult NoProduct(string productId) => Results.Problem(title: $"Product {productId} not found", statusCode: 404);

    private static IResult Bad(string title) => Results.Problem(title: title, statusCode: 400);

    // ── Catalog ────────────────────────────────────────────────────────────

    private static async Task<IResult> GetCatalog(string productId, ControlDbContext control, IProductDbContextFactory factory)
    {
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        return Results.Ok(await LoadCatalogAsync(product.Id, db));
    }

    private static async Task<CatalogDto> LoadCatalogAsync(string productId, ProductDbContext db)
    {
        var tags = await db.RuleTags.AsNoTracking().Where(t => t.ProductId == productId).ToListAsync();
        var rules = await db.CatalogRules.AsNoTracking().Where(r => r.ProductId == productId).ToListAsync();
        var policies = await db.RulePolicies.AsNoTracking().Where(p => p.ProductId == productId).ToListAsync();
        var ruleIds = rules.Select(r => r.Id).ToList();
        var policyIds = policies.Select(p => p.Id).ToList();
        var rulePicks = await db.WorkItemRules.AsNoTracking().Where(o => ruleIds.Contains(o.RuleId)).Select(o => new { Id = o.RuleId, o.On }).ToListAsync();
        var policyPicks = await db.WorkItemPolicies.AsNoTracking().Where(o => policyIds.Contains(o.PolicyId)).Select(o => new { Id = o.PolicyId, o.On }).ToListAsync();
        var scopes = await ScopesAsync(db, productId, null);
        return new CatalogDto(
            productId,
            policies.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(ToDto).ToList(),
            rules.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).Select(ToDto).ToList(),
            tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(ToDto).ToList(),
            rulePicks.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => new UsageDto(g.Count(p => p.On), g.Count(p => !p.On))),
            policyPicks.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => new UsageDto(g.Count(p => p.On), g.Count(p => !p.On))),
            scopes.GroupBy(s => s.PolicyId).ToDictionary(g => g.Key, g => (IReadOnlyList<ScopeDto>)g.Select(s => s.Scope).ToList()));
    }

    /// <summary>The product and story scopes of every policy (or of one), with story labels.</summary>
    private static async Task<List<(string PolicyId, ScopeDto Scope)>> ScopesAsync(ProductDbContext db, string productId, string? policyId)
    {
        var rows = await db.RulePolicyScopes.AsNoTracking()
            .Where(s => s.ProductId == productId && (policyId == null || s.PolicyId == policyId))
            .ToListAsync();
        var spawnIds = rows.Where(r => r.ScopeKind == PolicyScopes.Story).Select(r => r.ScopeId).Distinct().ToList();
        var stories = await db.StorySpawns.AsNoTracking()
            .Where(s => spawnIds.Contains(s.Id))
            .Select(s => new { s.Id, s.StoryId, s.StoryTitle })
            .ToDictionaryAsync(s => s.Id);
        return rows
            .OrderBy(r => r.ScopeKind == PolicyScopes.Product ? 0 : 1)
            .ThenBy(r => r.ScopeId, StringComparer.Ordinal)
            .Select(r => (r.PolicyId, new ScopeDto(
                r.ScopeKind,
                r.ScopeId,
                r.ScopeKind == PolicyScopes.Product
                    ? "the whole product"
                    : stories.TryGetValue(r.ScopeId, out var s) ? $"{s.StoryId} — {s.StoryTitle}" : $"{r.ScopeId} (story deleted)")))
            .ToList();
    }

    // ── Rules ──────────────────────────────────────────────────────────────

    /// <summary>Null when <paramref name="req"/> is a valid rule over the product's policies and tags, else why not.</summary>
    internal static string? ValidateRule(RuleRequest? req, IReadOnlyCollection<string> knownPolicyIds, IReadOnlyCollection<string>? knownTagIds = null)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Title)) return "title is required";
        if (req.Title.Trim().Length > MaxTitleLength) return $"title is longer than {MaxTitleLength} characters";
        if (string.IsNullOrWhiteSpace(req.Text)) return "text is required";
        if (req.Text.Trim().Length > MaxTextLength) return $"text is longer than {MaxTextLength} characters";
        var unknown = (req.PolicyIds ?? new List<string>()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && !knownPolicyIds.Contains(p));
        if (unknown is not null) return $"policy '{unknown}' is not in this product's catalog";
        var tag = (req.TagIds ?? new List<string>()).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t) && !(knownTagIds ?? Array.Empty<string>()).Contains(t));
        return tag is null ? null : $"tag '{tag}' is not in this product's catalog";
    }

    private static Task<List<string>> TagIdsAsync(ProductDbContext db, string productId) =>
        db.RuleTags.AsNoTracking().Where(t => t.ProductId == productId).Select(t => t.Id).ToListAsync();

    /// <summary>Makes exactly <paramref name="policyIds"/> contain <paramref name="ruleId"/> (appended at the end when added).</summary>
    private static void SetMembership(IEnumerable<RulePolicyRow> policies, string ruleId, ICollection<string> policyIds)
    {
        foreach (var p in policies)
        {
            var ids = RuleCatalogJson.ReadList(p.RuleIdsJson).ToList();
            var want = policyIds.Contains(p.Id);
            if (want == ids.Contains(ruleId)) continue;
            if (want) ids.Add(ruleId);
            else ids.Remove(ruleId);
            p.RuleIdsJson = RuleCatalogJson.WriteList(ids);
            p.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static async Task<IResult> AddRule(
        HttpContext ctx, string productId, [FromBody] RuleRequest req, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        var policies = await db.RulePolicies.Where(p => p.ProductId == product.Id).ToListAsync();
        if (ValidateRule(req, policies.Select(p => p.Id).ToList(), await TagIdsAsync(db, product.Id)) is { } bad) return Bad(bad);

        var row = new CatalogRuleRow
        {
            ProductId = product.Id,
            Title = req.Title!.Trim(),
            Text = req.Text!.Trim(),
            TagIdsJson = RuleCatalogJson.WriteList(req.TagIds),
            Enabled = req.Enabled ?? true,
            CreatedBy = ForgeUser.UserId(ctx),
        };
        db.CatalogRules.Add(row);
        if (req.PolicyIds is not null) SetMembership(policies, row.Id, req.PolicyIds);
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(row));
    }

    private static async Task<IResult> UpdateRule(
        HttpContext ctx, string productId, string ruleId, [FromBody] RuleRequest req, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        var policies = await db.RulePolicies.Where(p => p.ProductId == product.Id).ToListAsync();
        if (ValidateRule(req, policies.Select(p => p.Id).ToList(), await TagIdsAsync(db, product.Id)) is { } bad) return Bad(bad);

        var row = await db.CatalogRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.ProductId == product.Id);
        if (row is null) return Results.NotFound();
        row.Title = req.Title!.Trim();
        row.Text = req.Text!.Trim();
        if (req.TagIds is not null) row.TagIdsJson = RuleCatalogJson.WriteList(req.TagIds);
        row.Enabled = req.Enabled ?? row.Enabled;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        if (req.PolicyIds is not null) SetMembership(policies, row.Id, req.PolicyIds);
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(row));
    }

    /// <summary>Deletes the rule, takes it out of every policy, and drops every work item choice on it.</summary>
    private static async Task<IResult> DeleteRule(
        HttpContext ctx, string productId, string ruleId, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);

        var row = await db.CatalogRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.ProductId == product.Id);
        if (row is null) return Results.NotFound();
        SetMembership(await db.RulePolicies.Where(p => p.ProductId == product.Id).ToListAsync(), ruleId, Array.Empty<string>());
        db.WorkItemRules.RemoveRange(await db.WorkItemRules.Where(o => o.RuleId == ruleId).ToListAsync());
        db.CatalogRules.Remove(row);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    // ── Policies ───────────────────────────────────────────────────────────

    /// <summary>Null when <paramref name="req"/> is a valid policy over the product's rules and tags, else why not.</summary>
    internal static string? ValidatePolicy(PolicyRequest? req, IReadOnlyCollection<string> knownRuleIds, IReadOnlyCollection<string> knownTagIds)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Name)) return "name is required";
        if (req.Name.Trim().Length > MaxNameLength) return $"name is longer than {MaxNameLength} characters";
        var rule = (req.RuleIds ?? new List<string>()).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r) && !knownRuleIds.Contains(r));
        if (rule is not null) return $"rule '{rule}' is not in this product's catalog";
        var tag = (req.TagIds ?? new List<string>()).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t) && !knownTagIds.Contains(t));
        return tag is null ? null : $"tag '{tag}' is not in this product's catalog";
    }

    private static async Task<string?> ValidatePolicyAsync(ProductDbContext db, string productId, PolicyRequest req, string? exceptId)
    {
        var ruleIds = await db.CatalogRules.AsNoTracking().Where(r => r.ProductId == productId).Select(r => r.Id).ToListAsync();
        var tagIds = await db.RuleTags.AsNoTracking().Where(t => t.ProductId == productId).Select(t => t.Id).ToListAsync();
        if (ValidatePolicy(req, ruleIds, tagIds) is { } bad) return bad;
        var names = await db.RulePolicies.AsNoTracking().Where(p => p.ProductId == productId && p.Id != exceptId).Select(p => p.Name).ToListAsync();
        return names.Any(n => string.Equals(n, req.Name!.Trim(), StringComparison.OrdinalIgnoreCase))
            ? $"a policy named '{req.Name!.Trim()}' already exists"
            : null;
    }

    private static void Apply(RulePolicyRow row, PolicyRequest req)
    {
        row.Name = req.Name!.Trim();
        row.Description = req.Description?.Trim() ?? string.Empty;
        row.RuleIdsJson = RuleCatalogJson.WriteList(req.RuleIds);
        row.TagIdsJson = RuleCatalogJson.WriteList(req.TagIds);
        row.Enabled = req.Enabled ?? row.Enabled;
        row.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static async Task<IResult> AddPolicy(
        HttpContext ctx, string productId, [FromBody] PolicyRequest req, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        if (await ValidatePolicyAsync(db, product.Id, req, null) is { } bad) return Bad(bad);

        var row = new RulePolicyRow { ProductId = product.Id, CreatedBy = ForgeUser.UserId(ctx) };
        Apply(row, req);
        db.RulePolicies.Add(row);
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(row));
    }

    private static async Task<IResult> UpdatePolicy(
        HttpContext ctx, string productId, string policyId, [FromBody] PolicyRequest req, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        if (await ValidatePolicyAsync(db, product.Id, req, policyId) is { } bad) return Bad(bad);

        var row = await db.RulePolicies.FirstOrDefaultAsync(p => p.Id == policyId && p.ProductId == product.Id);
        if (row is null) return Results.NotFound();
        Apply(row, req);
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(row));
    }

    /// <summary>Deletes the policy (its rules stay in the catalog) and drops every work item choice and scope on it.</summary>
    private static async Task<IResult> DeletePolicy(
        HttpContext ctx, string productId, string policyId, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);

        var row = await db.RulePolicies.FirstOrDefaultAsync(p => p.Id == policyId && p.ProductId == product.Id);
        if (row is null) return Results.NotFound();
        db.WorkItemPolicies.RemoveRange(await db.WorkItemPolicies.Where(o => o.PolicyId == policyId).ToListAsync());
        db.RulePolicyScopes.RemoveRange(await db.RulePolicyScopes.Where(o => o.PolicyId == policyId).ToListAsync());
        db.RulePolicies.Remove(row);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    // ── Tags ───────────────────────────────────────────────────────────────

    /// <summary>Null when <paramref name="req"/> is a valid tag, else the reason it is not.</summary>
    internal static string? ValidateTag(TagRequest? req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Name)) return "name is required";
        if (req.Name.Trim().Length > MaxNameLength) return $"name is longer than {MaxNameLength} characters";
        foreach (var list in new[] { req.RepoIds, req.Categories, req.Phrases, req.Patterns })
        {
            if (list is { Count: > MaxListItems }) return $"a tag takes at most {MaxListItems} entries per list";
        }

        foreach (var category in req.Categories ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(category) && category.Trim() is not ("impl" or "qa-e2e"))
            {
                return $"category '{category}' is not impl or qa-e2e";
            }
        }

        foreach (var pattern in req.Patterns ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            if (RuleTagMatcher.ValidatePattern(pattern.Trim()) is { } why) return $"pattern '{pattern}' is not a valid regular expression: {why}";
        }

        return null;
    }

    private static void Apply(RuleTagRow row, TagRequest req)
    {
        row.Name = req.Name!.Trim();
        row.Description = req.Description?.Trim() ?? string.Empty;
        row.RepoIdsJson = RuleCatalogJson.WriteList(req.RepoIds);
        row.CategoriesJson = RuleCatalogJson.WriteList(req.Categories);
        row.PhrasesJson = RuleCatalogJson.WriteList(req.Phrases);
        row.PatternsJson = RuleCatalogJson.WriteList(req.Patterns);
        row.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static async Task<bool> TagNameTakenAsync(ProductDbContext db, string productId, string name, string? exceptId)
    {
        var names = await db.RuleTags.AsNoTracking()
            .Where(t => t.ProductId == productId && t.Id != exceptId)
            .Select(t => t.Name)
            .ToListAsync();
        return names.Any(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IResult> AddTag(
        HttpContext ctx, string productId, [FromBody] TagRequest req, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        if (ValidateTag(req) is { } bad) return Bad(bad);
        if (await TagNameTakenAsync(db, product.Id, req.Name!, null)) return Bad($"a tag named '{req.Name!.Trim()}' already exists");

        var row = new RuleTagRow { ProductId = product.Id, CreatedBy = ForgeUser.UserId(ctx) };
        Apply(row, req);
        db.RuleTags.Add(row);
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(row));
    }

    private static async Task<IResult> UpdateTag(
        HttpContext ctx, string productId, string tagId, [FromBody] TagRequest req, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        if (ValidateTag(req) is { } bad) return Bad(bad);

        var row = await db.RuleTags.FirstOrDefaultAsync(t => t.Id == tagId && t.ProductId == product.Id);
        if (row is null) return Results.NotFound();
        if (await TagNameTakenAsync(db, product.Id, req.Name!, tagId)) return Bad($"a tag named '{req.Name!.Trim()}' already exists");

        Apply(row, req);
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(row));
    }

    /// <summary>Deletes the tag, takes it off every policy and rule, and drops every work item choice on it.</summary>
    private static async Task<IResult> DeleteTag(
        HttpContext ctx, string productId, string tagId, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);

        var row = await db.RuleTags.FirstOrDefaultAsync(t => t.Id == tagId && t.ProductId == product.Id);
        if (row is null) return Results.NotFound();

        foreach (var policy in await db.RulePolicies.Where(p => p.ProductId == product.Id).ToListAsync())
        {
            var ids = RuleCatalogJson.ReadList(policy.TagIdsJson);
            if (!ids.Contains(tagId)) continue;
            policy.TagIdsJson = RuleCatalogJson.WriteList(ids.Where(i => i != tagId));
            policy.UpdatedAt = DateTimeOffset.UtcNow;
        }

        foreach (var rule in await db.CatalogRules.Where(r => r.ProductId == product.Id).ToListAsync())
        {
            var ids = RuleCatalogJson.ReadList(rule.TagIdsJson);
            if (!ids.Contains(tagId)) continue;
            rule.TagIdsJson = RuleCatalogJson.WriteList(ids.Where(i => i != tagId));
            rule.UpdatedAt = DateTimeOffset.UtcNow;
        }

        db.WorkstreamTagOverrides.RemoveRange(await db.WorkstreamTagOverrides.Where(o => o.TagId == tagId).ToListAsync());
        db.RuleTags.Remove(row);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    /// <summary>
    /// How one tag stands on every workstream of the product (or of one story
    /// when <paramref name="storyId"/> is given), so the operator can check a
    /// tag's matchers before relying on them.
    /// </summary>
    private static async Task<IResult> TagMatches(
        string productId, string tagId, [FromQuery] string? storyId, ControlDbContext control, IProductDbContextFactory factory)
    {
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);

        var tagRow = await db.RuleTags.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tagId && t.ProductId == product.Id);
        if (tagRow is null) return Results.NotFound();
        var tag = RuleTagSpec.FromRow(tagRow);

        var repoIds = await db.Repos.AsNoTracking().Where(r => r.ProductId == product.Id).Select(r => r.Id).ToListAsync();
        var query = db.Workstreams.AsNoTracking().Where(w => w.RepoId != null && repoIds.Contains(w.RepoId));
        if (!string.IsNullOrWhiteSpace(storyId))
        {
            var spawnId = await db.StorySpawns.AsNoTracking().Where(s => s.StoryId == storyId).Select(s => s.Id).FirstOrDefaultAsync();
            if (spawnId is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);
            query = query.Where(w => w.StorySpawnId == spawnId);
        }

        var workstreams = await query.Select(w => new { w.Id, w.Name, w.Category, w.Status, w.RepoId, w.Summary }).ToListAsync();
        var ids = workstreams.Select(w => w.Id).ToList();
        var items = await db.WorkItems.AsNoTracking()
            .Where(w => ids.Contains(w.Id))
            .Select(w => new { w.Id, w.Title, w.DescriptionMd })
            .ToDictionaryAsync(w => w.Id);
        var overrides = await db.WorkstreamTagOverrides.AsNoTracking()
            .Where(o => o.TagId == tagId)
            .ToDictionaryAsync(o => o.WorkstreamId, o => o.On);

        var result = new List<TagMatchDto>();
        foreach (var w in workstreams)
        {
            var category = string.IsNullOrEmpty(w.Category) ? "impl" : w.Category;
            if (overrides.TryGetValue(w.Id, out var on))
            {
                result.Add(new TagMatchDto(w.Id, w.Name, category, w.Status, on, on ? "operator-added" : "operator-removed",
                    on ? "added by the operator" : "removed by the operator"));
                continue;
            }

            items.TryGetValue(w.Id, out var wi);
            var text = $"{wi?.Title ?? w.Name}\n{(string.IsNullOrEmpty(wi?.DescriptionMd) ? w.Summary : wi!.DescriptionMd)}";
            var m = RuleTagMatcher.Match(tag, new RuleSubject(w.Id, w.RepoId, category, text));
            result.Add(new TagMatchDto(w.Id, w.Name, category, w.Status, m.Matched, m.Matched ? "matched" : "no-match", m.Reason));
        }

        return Results.Ok(result.OrderByDescending(r => r.Applied).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    // ── Where a policy applies ─────────────────────────────────────────────

    /// <summary>Where the operator applied a policy: whole product or stories, and single work items (attached or removed).</summary>
    private static async Task<IResult> PolicyAssignments(
        string productId, string policyId, ControlDbContext control, IProductDbContextFactory factory)
    {
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        if (!await db.RulePolicies.AsNoTracking().AnyAsync(p => p.Id == policyId && p.ProductId == product.Id)) return Results.NotFound();
        return Results.Ok(await AssignmentsAsync(db, product.Id, policyId));
    }

    private static async Task<AssignmentsDto> AssignmentsAsync(ProductDbContext db, string productId, string policyId)
    {
        var scopes = (await ScopesAsync(db, productId, policyId)).Select(s => s.Scope).ToList();
        var picks = await db.WorkItemPolicies.AsNoTracking().Where(o => o.PolicyId == policyId).ToListAsync();
        var names = await TargetNamesAsync(db, picks.Select(p => p.WorkItemId).ToList());
        var items = picks
            .Select(p => names.TryGetValue(p.WorkItemId, out var n)
                ? new AssignedWorkItemDto(p.WorkItemId, n.Title, n.Kind, n.StoryId, p.On)
                : new AssignedWorkItemDto(p.WorkItemId, p.WorkItemId, "work item", null, p.On))
            .OrderBy(i => i.StoryId ?? "~", StringComparer.Ordinal)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AssignmentsDto(policyId, scopes, items);
    }

    /// <summary>Title, kind and story of each workstream or work item id.</summary>
    private static async Task<Dictionary<string, WorkItemTargetDto>> TargetNamesAsync(ProductDbContext db, List<string> ids)
    {
        var workstreams = await db.Workstreams.AsNoTracking()
            .Where(w => ids.Contains(w.Id))
            .Select(w => new { w.Id, w.Name, w.Status, w.StorySpawnId })
            .ToListAsync();
        var items = await db.WorkItems.AsNoTracking()
            .Where(w => ids.Contains(w.Id))
            .Select(w => new { w.Id, w.Title, w.State })
            .ToDictionaryAsync(w => w.Id);
        var spawnIds = workstreams.Where(w => w.StorySpawnId != null).Select(w => w.StorySpawnId!).Distinct().ToList();
        var stories = await db.StorySpawns.AsNoTracking().Where(s => spawnIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.StoryId);

        var result = new Dictionary<string, WorkItemTargetDto>(StringComparer.Ordinal);
        foreach (var w in workstreams)
        {
            var title = items.TryGetValue(w.Id, out var wi) && !string.IsNullOrWhiteSpace(wi.Title) ? wi.Title : w.Name;
            var story = w.StorySpawnId is not null && stories.TryGetValue(w.StorySpawnId, out var s) ? s : null;
            result[w.Id] = new WorkItemTargetDto(w.Id, title, "workstream", story, w.Status);
        }

        foreach (var (id, wi) in items)
        {
            if (!result.ContainsKey(id)) result[id] = new WorkItemTargetDto(id, wi.Title, "work item", null, wi.State);
        }

        return result;
    }

    /// <summary>
    /// Applies a policy to a whole scope: <paramref name="kind"/> <c>product</c>
    /// (<paramref name="scopeId"/> is the product id) or <c>story</c>
    /// (<paramref name="scopeId"/> is a story spawn id of this product).
    /// </summary>
    private static async Task<IResult> SetPolicyScope(
        HttpContext ctx, string productId, string policyId, string kind, string scopeId, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        if (!await db.RulePolicies.AsNoTracking().AnyAsync(p => p.Id == policyId && p.ProductId == product.Id)) return Results.NotFound();
        if (await ValidateScopeAsync(db, product, kind, scopeId) is { } bad) return bad;

        var key = kind == PolicyScopes.Product ? product.Id : scopeId;
        if (!await db.RulePolicyScopes.AnyAsync(s => s.PolicyId == policyId && s.ScopeKind == kind && s.ScopeId == key))
        {
            db.RulePolicyScopes.Add(new RulePolicyScopeRow
            {
                ProductId = product.Id, PolicyId = policyId, ScopeKind = kind, ScopeId = key, CreatedBy = ForgeUser.UserId(ctx),
            });
            await db.SaveChangesAsync();
        }

        return Results.Ok(await AssignmentsAsync(db, product.Id, policyId));
    }

    private static async Task<IResult> ClearPolicyScope(
        HttpContext ctx, string productId, string policyId, string kind, string scopeId, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        if (!await db.RulePolicies.AsNoTracking().AnyAsync(p => p.Id == policyId && p.ProductId == product.Id)) return Results.NotFound();
        if (kind is not (PolicyScopes.Product or PolicyScopes.Story)) return Bad($"'{kind}' is not product or story");

        var key = kind == PolicyScopes.Product ? product.Id : scopeId;
        db.RulePolicyScopes.RemoveRange(await db.RulePolicyScopes.Where(s => s.PolicyId == policyId && s.ScopeKind == kind && s.ScopeId == key).ToListAsync());
        await db.SaveChangesAsync();
        return Results.Ok(await AssignmentsAsync(db, product.Id, policyId));
    }

    private static async Task<IResult?> ValidateScopeAsync(ProductDbContext db, ProductRow product, string kind, string scopeId)
    {
        switch (kind)
        {
            case PolicyScopes.Product:
                return scopeId == product.Id || scopeId == product.Slug ? null : Bad($"'{scopeId}' is not this product");
            case PolicyScopes.Story:
                var repoIds = await db.Repos.AsNoTracking().Where(r => r.ProductId == product.Id).Select(r => r.Id).ToListAsync();
                var inProduct = await db.StorySpawns.AsNoTracking().AnyAsync(s => s.Id == scopeId && s.RepoId != null && repoIds.Contains(s.RepoId));
                return inProduct ? null : Results.Problem(title: $"Story {scopeId} is not in product {product.Id}", statusCode: 404);
            default:
                return Bad($"'{kind}' is not product or story");
        }
    }

    /// <summary>
    /// What a policy can be applied to: every story of the product, and the
    /// workstreams (most recently active first) and, when <paramref name="q"/>
    /// is given, standalone work items whose id or title contains it; at most 50.
    /// </summary>
    private static async Task<IResult> Targets(
        string productId, [FromQuery] string? q, ControlDbContext control, IProductDbContextFactory factory)
    {
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);

        var repoIds = await db.Repos.AsNoTracking().Where(r => r.ProductId == product.Id).Select(r => r.Id).ToListAsync();
        var spawns = await db.StorySpawns.AsNoTracking()
            .Where(s => s.RepoId != null && repoIds.Contains(s.RepoId))
            .Select(s => new { s.Id, s.StoryId, s.StoryTitle, s.Phase })
            .ToListAsync();
        var workstreams = await db.Workstreams.AsNoTracking()
            .Where(w => w.RepoId != null && repoIds.Contains(w.RepoId))
            .Select(w => new { w.Id, w.Name, w.Status, w.StorySpawnId, w.LastActive })
            .ToListAsync();
        var counts = workstreams.Where(w => w.StorySpawnId != null).GroupBy(w => w.StorySpawnId!).ToDictionary(g => g.Key, g => g.Count());
        var stories = spawns
            .Select(s => new StoryTargetDto(s.Id, s.StoryId, s.StoryTitle, s.Phase, counts.GetValueOrDefault(s.Id)))
            .OrderBy(s => s.StoryId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var term = q?.Trim() ?? string.Empty;
        var wsIds = workstreams.Select(w => w.Id).ToList();
        var titles = await db.WorkItems.AsNoTracking()
            .Where(w => wsIds.Contains(w.Id))
            .Select(w => new { w.Id, w.Title })
            .ToDictionaryAsync(w => w.Id, w => w.Title);
        var storyOf = spawns.ToDictionary(s => s.Id, s => s.StoryId);
        var found = workstreams
            .Select(w => new
            {
                Dto = new WorkItemTargetDto(
                    w.Id,
                    titles.TryGetValue(w.Id, out var t) && !string.IsNullOrWhiteSpace(t) ? t : w.Name,
                    "workstream",
                    w.StorySpawnId is not null && storyOf.TryGetValue(w.StorySpawnId, out var sid) ? sid : null,
                    w.Status),
                w.LastActive,
            })
            .Where(x => term.Length == 0 || Contains(x.Dto, term))
            .OrderByDescending(x => x.LastActive)
            .Take(MaxTargets)
            .Select(x => x.Dto)
            .ToList();

        if (term.Length > 0 && found.Count < MaxTargets)
        {
            var like = $"%{term}%";
            var items = await db.WorkItems.AsNoTracking()
                .Where(w => w.RepoId != null && repoIds.Contains(w.RepoId) && !wsIds.Contains(w.Id))
                .Where(w => term.Length == 0 || EF.Functions.Like(w.Title, like) || EF.Functions.Like(w.Id, like))
                .OrderBy(w => w.Id)
                .Take(MaxTargets - found.Count)
                .Select(w => new { w.Id, w.Title, w.State })
                .ToListAsync();
            found.AddRange(items.Select(w => new WorkItemTargetDto(w.Id, w.Title, "work item", null, w.State)));
        }

        return Results.Ok(new TargetsDto(stories, found));
    }

    private static bool Contains(WorkItemTargetDto t, string term) =>
        t.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
        || t.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (t.StoryId?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// How many workstreams of the given stories (comma-separated spawn ids)
    /// each policy and rule reaches, resolved exactly as their prompts are.
    /// </summary>
    private static async Task<IResult> Reach(
        string productId, [FromQuery] string? spawnIds, ControlDbContext control, IProductDbContextFactory factory)
    {
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);

        var ids = (spawnIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        var repoIds = await db.Repos.AsNoTracking().Where(r => r.ProductId == product.Id).Select(r => r.Id).ToListAsync();
        var workstreams = await db.Workstreams.AsNoTracking()
            .Where(w => w.StorySpawnId != null && ids.Contains(w.StorySpawnId) && w.RepoId != null && repoIds.Contains(w.RepoId))
            .Select(w => new { w.Id, w.Name, w.Category, w.RepoId, w.Summary, w.StorySpawnId })
            .ToListAsync();
        var wsIds = workstreams.Select(w => w.Id).ToList();
        var items = await db.WorkItems.AsNoTracking()
            .Where(w => wsIds.Contains(w.Id))
            .Select(w => new { w.Id, w.Title, w.DescriptionMd })
            .ToDictionaryAsync(w => w.Id);
        var subjects = workstreams.Select(w =>
        {
            items.TryGetValue(w.Id, out var wi);
            var text = $"{wi?.Title ?? w.Name}\n{(string.IsNullOrEmpty(wi?.DescriptionMd) ? w.Summary : wi!.DescriptionMd)}";
            return new RuleSubject(w.Id, w.RepoId, string.IsNullOrEmpty(w.Category) ? "impl" : w.Category, text, w.StorySpawnId);
        }).ToList();

        var resolved = await RuleCatalogResolver.ResolveManyAsync(db, product.Id, subjects, CancellationToken.None);
        var policies = resolved.SelectMany(r => r.Policies.Where(p => p.Applied).Select(p => p.Id))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        var rules = resolved.SelectMany(r => r.Rules.Select(x => x.Id))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        return Results.Ok(new ReachDto(subjects.Count, policies, rules));
    }

    // ── One work item ──────────────────────────────────────────────────────

    private static async Task<IResult> WorkItemRulesFor(
        string productId, string workItemId, ControlDbContext control, IProductDbContextFactory factory)
    {
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        return await ResolvedOrNotFoundAsync(db, product.Id, workItemId);
    }

    private static async Task<IResult> ResolvedOrNotFoundAsync(ProductDbContext db, string productId, string workItemId)
    {
        var resolved = await RuleCatalogResolver.ResolveAsync(db, workItemId, CancellationToken.None);
        return resolved is null || resolved.ProductId != productId ? NotInProduct(workItemId, productId) : Results.Ok(resolved);
    }

    private static IResult NotInProduct(string workItemId, string productId) =>
        Results.Problem(title: $"Work item {workItemId} is not in product {productId}", statusCode: 404);

    private static async Task<bool> InProductAsync(ProductDbContext db, string productId, string workItemId)
    {
        var subject = await RuleCatalogResolver.LoadSubjectAsync(db, workItemId, CancellationToken.None);
        return subject is not null && subject.Value.ProductId == productId;
    }

    /// <summary>
    /// Attaches (<c>on: true</c>) or detaches (<c>on: false</c>) a policy, a
    /// rule or a tag on one work item; <paramref name="kind"/> is
    /// <c>policies</c>, <c>rules</c> or <c>tags</c>.
    /// </summary>
    private static async Task<IResult> SetWorkItemChoice(
        HttpContext ctx, string productId, string workItemId, string kind, string id, [FromBody] OnRequest req,
        ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        if (!await InProductAsync(db, product.Id, workItemId)) return NotInProduct(workItemId, product.Id);

        var by = ForgeUser.UserId(ctx);
        var now = DateTimeOffset.UtcNow;
        switch (kind)
        {
            case "policies":
                if (!await db.RulePolicies.AsNoTracking().AnyAsync(p => p.Id == id && p.ProductId == product.Id)) return Results.NotFound();
                var p = await db.WorkItemPolicies.FirstOrDefaultAsync(o => o.WorkItemId == workItemId && o.PolicyId == id);
                if (p is null) db.WorkItemPolicies.Add(p = new WorkItemPolicyRow { WorkItemId = workItemId, PolicyId = id });
                (p.On, p.CreatedAt, p.CreatedBy) = (req.On, now, by);
                break;
            case "rules":
                if (!await db.CatalogRules.AsNoTracking().AnyAsync(r => r.Id == id && r.ProductId == product.Id)) return Results.NotFound();
                var r = await db.WorkItemRules.FirstOrDefaultAsync(o => o.WorkItemId == workItemId && o.RuleId == id);
                if (r is null) db.WorkItemRules.Add(r = new WorkItemRuleRow { WorkItemId = workItemId, RuleId = id });
                (r.On, r.CreatedAt, r.CreatedBy) = (req.On, now, by);
                break;
            case "tags":
                if (!await db.RuleTags.AsNoTracking().AnyAsync(t => t.Id == id && t.ProductId == product.Id)) return Results.NotFound();
                var t = await db.WorkstreamTagOverrides.FirstOrDefaultAsync(o => o.WorkstreamId == workItemId && o.TagId == id);
                if (t is null) db.WorkstreamTagOverrides.Add(t = new WorkstreamTagOverrideRow { WorkstreamId = workItemId, TagId = id });
                (t.On, t.CreatedAt, t.CreatedBy) = (req.On, now, by);
                break;
            default:
                return Bad($"'{kind}' is not policies, rules or tags");
        }

        await db.SaveChangesAsync();
        return await ResolvedOrNotFoundAsync(db, product.Id, workItemId);
    }

    /// <summary>Drops the operator's choice on a policy, rule or tag for one work item, so the automatic choice applies again.</summary>
    private static async Task<IResult> ClearWorkItemChoice(
        HttpContext ctx, string productId, string workItemId, string kind, string id, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        if (!await InProductAsync(db, product.Id, workItemId)) return NotInProduct(workItemId, product.Id);

        switch (kind)
        {
            case "policies":
                db.WorkItemPolicies.RemoveRange(await db.WorkItemPolicies.Where(o => o.WorkItemId == workItemId && o.PolicyId == id).ToListAsync());
                break;
            case "rules":
                db.WorkItemRules.RemoveRange(await db.WorkItemRules.Where(o => o.WorkItemId == workItemId && o.RuleId == id).ToListAsync());
                break;
            case "tags":
                db.WorkstreamTagOverrides.RemoveRange(await db.WorkstreamTagOverrides.Where(o => o.WorkstreamId == workItemId && o.TagId == id).ToListAsync());
                break;
            default:
                return Bad($"'{kind}' is not policies, rules or tags");
        }

        await db.SaveChangesAsync();
        return await ResolvedOrNotFoundAsync(db, product.Id, workItemId);
    }
}