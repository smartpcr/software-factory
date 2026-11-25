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
/// Phase 108 — the product rule catalog. Tags say which workstreams they apply
/// to (repos, categories, phrases and patterns over the stage text); rules are
/// attached to tags and reach every matching workstream's generator prompt.
/// The operator can add or remove a tag on one workstream, which wins over the
/// matchers. Reads are open to product readers; writes need a paired operator.
/// </summary>
public static class RuleCatalogEndpoints
{
    private const int MaxNameLength = 80;
    private const int MaxTitleLength = 120;
    private const int MaxTextLength = 8000;
    private const int MaxListItems = 50;

    public static IEndpointRouteBuilder MapRuleCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/products/{productId}/rule-catalog");
        grp.MapGet(string.Empty, GetCatalog).WithName("GetRuleCatalog");
        grp.MapPost("/tags", AddTag).WithName("AddRuleTag");
        grp.MapPut("/tags/{tagId}", UpdateTag).WithName("UpdateRuleTag");
        grp.MapDelete("/tags/{tagId}", DeleteTag).WithName("DeleteRuleTag");
        grp.MapGet("/tags/{tagId}/matches", TagMatches).WithName("ListRuleTagMatches");
        grp.MapPost("/rules", AddRule).WithName("AddCatalogRule");
        grp.MapPut("/rules/{ruleId}", UpdateRule).WithName("UpdateCatalogRule");
        grp.MapDelete("/rules/{ruleId}", DeleteRule).WithName("DeleteCatalogRule");
        grp.MapGet("/workstreams/{workstreamId}", WorkstreamRulesFor).WithName("GetWorkstreamRules");
        grp.MapPut("/workstreams/{workstreamId}/tags/{tagId}", SetWorkstreamTag).WithName("SetWorkstreamTag");
        grp.MapDelete("/workstreams/{workstreamId}/tags/{tagId}", ClearWorkstreamTag).WithName("ClearWorkstreamTag");
        return app;
    }

    public sealed record TagRequest(
        string? Name,
        string? Description,
        List<string>? RepoIds,
        List<string>? Categories,
        List<string>? Phrases,
        List<string>? Patterns);

    public sealed record RuleRequest(string? Title, string? Text, List<string>? TagIds, bool? Enabled);

    public sealed record WorkstreamTagRequest(bool On);

    public sealed record TagDto(
        string Id,
        string Name,
        string Description,
        IReadOnlyList<string> RepoIds,
        IReadOnlyList<string> Categories,
        IReadOnlyList<string> Phrases,
        IReadOnlyList<string> Patterns,
        DateTimeOffset CreatedAt,
        string? CreatedBy,
        DateTimeOffset UpdatedAt);

    public sealed record RuleDto(
        string Id,
        string Title,
        string Text,
        IReadOnlyList<string> TagIds,
        bool Enabled,
        DateTimeOffset CreatedAt,
        string? CreatedBy,
        DateTimeOffset UpdatedAt);

    public sealed record CatalogDto(string ProductId, IReadOnlyList<TagDto> Tags, IReadOnlyList<RuleDto> Rules);

    public sealed record TagMatchDto(string WorkstreamId, string Name, string Category, string Status, bool Applied, string Source, string Reason);

    private static TagDto ToDto(RuleTagRow r) => new(
        r.Id,
        r.Name,
        r.Description,
        RuleCatalogJson.ReadList(r.RepoIdsJson),
        RuleCatalogJson.ReadList(r.CategoriesJson),
        RuleCatalogJson.ReadList(r.PhrasesJson),
        RuleCatalogJson.ReadList(r.PatternsJson),
        r.CreatedAt,
        r.CreatedBy,
        r.UpdatedAt);

    private static RuleDto ToDto(CatalogRuleRow r) =>
        new(r.Id, r.Title, r.Text, RuleCatalogJson.ReadList(r.TagIdsJson), r.Enabled, r.CreatedAt, r.CreatedBy, r.UpdatedAt);

    private static async Task<(ProductRow? Product, ProductDbContext? Db)> ResolveProductAsync(
        string productId, ControlDbContext control, IProductDbContextFactory factory)
    {
        var product = await control.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId || p.Slug == productId);
        return product is null ? (null, null) : (product, factory.CreateFor(product.Slug));
    }

    private static IResult NoProduct(string productId) => Results.Problem(title: $"Product {productId} not found", statusCode: 404);

    private static IResult Bad(string title) => Results.Problem(title: title, statusCode: 400);

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
        return new CatalogDto(
            productId,
            tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(ToDto).ToList(),
            rules.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).Select(ToDto).ToList());
    }

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

    private static async Task<bool> NameTakenAsync(ProductDbContext db, string productId, string name, string? exceptId)
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
        if (await NameTakenAsync(db, product.Id, req.Name!, null)) return Bad($"a tag named '{req.Name!.Trim()}' already exists");

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
        if (await NameTakenAsync(db, product.Id, req.Name!, tagId)) return Bad($"a tag named '{req.Name!.Trim()}' already exists");

        Apply(row, req);
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(row));
    }

    /// <summary>Deletes the tag, drops it from every rule, and drops every workstream override on it.</summary>
    private static async Task<IResult> DeleteTag(
        HttpContext ctx, string productId, string tagId, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);

        var row = await db.RuleTags.FirstOrDefaultAsync(t => t.Id == tagId && t.ProductId == product.Id);
        if (row is null) return Results.NotFound();

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
        return Results.Ok(await LoadCatalogAsync(product.Id, db));
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
            items.TryGetValue(w.Id, out var wi);
            var text = $"{wi?.Title ?? w.Name}\n{(string.IsNullOrEmpty(wi?.DescriptionMd) ? w.Summary : wi!.DescriptionMd)}";
            var subject = new RuleSubject(w.Id, w.RepoId, category, text);
            var one = new Dictionary<string, bool>();
            if (overrides.TryGetValue(w.Id, out var on)) one[tagId] = on;
            var applied = RuleCatalogResolver.Resolve(subject, product.Id, new[] { tag }, one, Array.Empty<RuleSpec>()).Tags[0];
            result.Add(new TagMatchDto(w.Id, w.Name, category, w.Status, applied.Applied, applied.Source, applied.Reason));
        }

        return Results.Ok(result.OrderByDescending(r => r.Applied).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Null when <paramref name="req"/> is a valid rule over <paramref name="knownTagIds"/>, else why not.</summary>
    internal static string? ValidateRule(RuleRequest? req, IReadOnlyCollection<string> knownTagIds)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Title)) return "title is required";
        if (req.Title.Trim().Length > MaxTitleLength) return $"title is longer than {MaxTitleLength} characters";
        if (string.IsNullOrWhiteSpace(req.Text)) return "text is required";
        if (req.Text.Trim().Length > MaxTextLength) return $"text is longer than {MaxTextLength} characters";
        var tagIds = (req.TagIds ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (tagIds.Count == 0) return "a rule needs at least one tag; it reaches the workstreams its tags apply to";
        var unknown = tagIds.FirstOrDefault(t => !knownTagIds.Contains(t));
        return unknown is null ? null : $"tag '{unknown}' is not in this product's catalog";
    }

    private static async Task<List<string>> TagIdsAsync(ProductDbContext db, string productId) =>
        await db.RuleTags.AsNoTracking().Where(t => t.ProductId == productId).Select(t => t.Id).ToListAsync();

    private static async Task<IResult> AddRule(
        HttpContext ctx, string productId, [FromBody] RuleRequest req, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        if (ValidateRule(req, await TagIdsAsync(db, product.Id)) is { } bad) return Bad(bad);

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
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(row));
    }

    private static async Task<IResult> UpdateRule(
        HttpContext ctx, string productId, string ruleId, [FromBody] RuleRequest req, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        if (ValidateRule(req, await TagIdsAsync(db, product.Id)) is { } bad) return Bad(bad);

        var row = await db.CatalogRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.ProductId == product.Id);
        if (row is null) return Results.NotFound();
        row.Title = req.Title!.Trim();
        row.Text = req.Text!.Trim();
        row.TagIdsJson = RuleCatalogJson.WriteList(req.TagIds);
        row.Enabled = req.Enabled ?? row.Enabled;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(row));
    }

    private static async Task<IResult> DeleteRule(
        HttpContext ctx, string productId, string ruleId, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);

        var row = await db.CatalogRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.ProductId == product.Id);
        if (row is null) return Results.NotFound();
        db.CatalogRules.Remove(row);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> WorkstreamRulesFor(
        string productId, string workstreamId, ControlDbContext control, IProductDbContextFactory factory)
    {
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        return await ResolvedOrNotFoundAsync(db, product.Id, workstreamId);
    }

    private static async Task<IResult> ResolvedOrNotFoundAsync(ProductDbContext db, string productId, string workstreamId)
    {
        var resolved = await RuleCatalogResolver.ResolveAsync(db, workstreamId, CancellationToken.None);
        if (resolved is null || resolved.ProductId != productId)
        {
            return Results.Problem(title: $"Workstream {workstreamId} is not in product {productId}", statusCode: 404);
        }

        return Results.Ok(resolved);
    }

    /// <summary>Adds (<c>on: true</c>) or removes (<c>on: false</c>) a tag on one workstream, over its matchers.</summary>
    private static async Task<IResult> SetWorkstreamTag(
        HttpContext ctx,
        string productId,
        string workstreamId,
        string tagId,
        [FromBody] WorkstreamTagRequest req,
        ControlDbContext control,
        IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);
        if (!await db.RuleTags.AsNoTracking().AnyAsync(t => t.Id == tagId && t.ProductId == product.Id)) return Results.NotFound();
        var subject = await RuleCatalogResolver.LoadSubjectAsync(db, workstreamId, CancellationToken.None);
        if (subject is null || subject.Value.ProductId != product.Id)
        {
            return Results.Problem(title: $"Workstream {workstreamId} is not in product {product.Id}", statusCode: 404);
        }

        var row = await db.WorkstreamTagOverrides.FirstOrDefaultAsync(o => o.WorkstreamId == workstreamId && o.TagId == tagId);
        if (row is null)
        {
            row = new WorkstreamTagOverrideRow { WorkstreamId = workstreamId, TagId = tagId };
            db.WorkstreamTagOverrides.Add(row);
        }

        row.On = req.On;
        row.CreatedAt = DateTimeOffset.UtcNow;
        row.CreatedBy = ForgeUser.UserId(ctx);
        await db.SaveChangesAsync();
        return await ResolvedOrNotFoundAsync(db, product.Id, workstreamId);
    }

    /// <summary>Drops the operator's decision on a tag, so its matchers decide again.</summary>
    private static async Task<IResult> ClearWorkstreamTag(
        HttpContext ctx, string productId, string workstreamId, string tagId, ControlDbContext control, IProductDbContextFactory factory)
    {
        if (ForgeUser.RequireWriter(ctx) is { } auth) return auth;
        var (product, db) = await ResolveProductAsync(productId, control, factory);
        if (product is null || db is null) return NoProduct(productId);

        var row = await db.WorkstreamTagOverrides.FirstOrDefaultAsync(o => o.WorkstreamId == workstreamId && o.TagId == tagId);
        if (row is not null)
        {
            db.WorkstreamTagOverrides.Remove(row);
            await db.SaveChangesAsync();
        }

        return await ResolvedOrNotFoundAsync(db, product.Id, workstreamId);
    }
}