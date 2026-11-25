// -----------------------------------------------------------------------
// <copyright file="RuleCatalog.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Rules;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>A catalog tag as the matcher sees it (<see cref="RuleTagRow"/> with its JSON lists parsed).</summary>
public sealed record RuleTagSpec(
    string Id,
    string Name,
    IReadOnlyList<string> RepoIds,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Phrases,
    IReadOnlyList<string> Patterns)
{
    public static RuleTagSpec FromRow(RuleTagRow row) => new(
        row.Id,
        row.Name,
        RuleCatalogJson.ReadList(row.RepoIdsJson),
        RuleCatalogJson.ReadList(row.CategoriesJson),
        RuleCatalogJson.ReadList(row.PhrasesJson),
        RuleCatalogJson.ReadList(row.PatternsJson));
}

/// <summary>A catalog rule as the resolver sees it.</summary>
public sealed record RuleSpec(string Id, string Title, string Text, IReadOnlyList<string> TagIds, bool Enabled)
{
    public static RuleSpec FromRow(CatalogRuleRow row) =>
        new(row.Id, row.Title, row.Text, RuleCatalogJson.ReadList(row.TagIdsJson), row.Enabled);
}

/// <summary>
/// What a tag is matched against: the work item's repo and category, and its
/// text (title and description, which for a workstream carry the plan stage's
/// steps and test scenarios, plus a standalone work item's own tags).
/// </summary>
public sealed record RuleSubject(string WorkItemId, string? RepoId, string Category, string Text);

/// <summary>The matcher's verdict on one tag, with a reason the operator can read.</summary>
public sealed record TagMatch(bool Matched, string Reason);

/// <summary>
/// How one tag stands on one workstream. <see cref="Source"/> is
/// <c>matched</c> or <c>no-match</c> when the matchers decided, and
/// <c>operator-added</c> or <c>operator-removed</c> when an override did.
/// </summary>
public sealed record AppliedTag(string TagId, string Name, bool Applied, string Source, string Reason);

/// <summary>
/// A rule that reaches a work item: <see cref="Attached"/> when the operator
/// attached it to this item, else <see cref="Via"/> names the applied tags
/// that brought it.
/// </summary>
public sealed record AppliedRule(string Id, string Title, string Text, IReadOnlyList<string> Via, bool Attached);

/// <summary>
/// How one catalog rule stands on a work item, for the operator's picker.
/// <see cref="Source"/> is <c>tag</c> (one of its tags applies),
/// <c>attached</c> / <c>detached</c> (the operator's choice for this item),
/// <c>none</c> (no tag applies) or <c>disabled</c> (switched off in the catalog).
/// </summary>
public sealed record RuleChoice(string Id, string Title, string Text, bool Enabled, bool Applied, string Source, IReadOnlyList<string> Via);

/// <summary>Every catalog tag's and rule's standing on a work item, and the rules that reach its prompt.</summary>
public sealed record WorkItemRules(
    string WorkItemId,
    string ProductId,
    IReadOnlyList<AppliedTag> Tags,
    IReadOnlyList<AppliedRule> Rules,
    IReadOnlyList<RuleChoice> Choices);

/// <summary>Decides whether a catalog tag applies to a workstream.</summary>
public static class RuleTagMatcher
{
    /// <summary>Bound on one pattern's evaluation, so an operator's regex can never stall a prompt build.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private const int MaxQuoted = 3;

    /// <summary>
    /// A tag applies when the workstream is in one of its repos and categories
    /// (an empty list allows all) and its text contains one of the phrases or
    /// matches one of the patterns, case-insensitively. A tag with neither
    /// phrases nor patterns applies to every workstream it allows.
    /// </summary>
    public static TagMatch Match(RuleTagSpec tag, RuleSubject subject)
    {
        if (tag.RepoIds.Count > 0 && !tag.RepoIds.Contains(subject.RepoId ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return new TagMatch(false, $"repo {subject.RepoId ?? "(none)"} is not one of {string.Join(", ", tag.RepoIds)}");
        }

        if (tag.Categories.Count > 0 && !tag.Categories.Contains(subject.Category, StringComparer.OrdinalIgnoreCase))
        {
            return new TagMatch(false, $"category {subject.Category} is not one of {string.Join(", ", tag.Categories)}");
        }

        if (tag.Phrases.Count == 0 && tag.Patterns.Count == 0)
        {
            return new TagMatch(true, Scope(tag));
        }

        var hits = new List<string>();
        foreach (var phrase in tag.Phrases)
        {
            if (!string.IsNullOrWhiteSpace(phrase) && subject.Text.Contains(phrase.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                hits.Add($"\"{phrase.Trim()}\"");
            }
        }

        foreach (var pattern in tag.Patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                var m = Regex.Match(subject.Text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                if (m.Success) hits.Add($"/{pattern}/ (\"{Clip(m.Value)}\")");
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                // Validated on save; a pattern that still fails here is skipped, not fatal.
            }
        }

        if (hits.Count == 0) return new TagMatch(false, "no phrase or pattern found in the stage text");
        var quoted = string.Join(", ", hits.Take(MaxQuoted)) + (hits.Count > MaxQuoted ? $" and {hits.Count - MaxQuoted} more" : string.Empty);
        return new TagMatch(true, $"matched {quoted}");
    }

    /// <summary>Null when <paramref name="pattern"/> is a usable regular expression, else why not.</summary>
    public static string? ValidatePattern(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    private static string Scope(RuleTagSpec tag)
    {
        var repos = tag.RepoIds.Count == 0 ? "every repo" : string.Join(", ", tag.RepoIds);
        var cats = tag.Categories.Count == 0 ? "every workstream" : $"{string.Join(", ", tag.Categories)} workstreams";
        return $"{cats} in {repos}";
    }

    private static string Clip(string s) => s.Length <= 60 ? s : s[..57] + "...";
}

/// <summary>
/// Resolves which catalog tags and rules reach a work item execution or a
/// workstream, and renders the rules as the prompt block the generator sees
/// on every iteration.
/// </summary>
public static class RuleCatalogResolver
{
    private static readonly IReadOnlyDictionary<string, bool> NoOverrides = new Dictionary<string, bool>();

    /// <summary>
    /// Pure resolution. A tag override wins over the tag's matchers; a rule
    /// override (attached / detached) wins over the rule's tags; a disabled
    /// rule reaches nothing.
    /// </summary>
    public static WorkItemRules Resolve(
        RuleSubject subject,
        string productId,
        IEnumerable<RuleTagSpec> tags,
        IReadOnlyDictionary<string, bool> tagOverrides,
        IEnumerable<RuleSpec> rules,
        IReadOnlyDictionary<string, bool>? ruleOverrides = null)
    {
        ruleOverrides ??= NoOverrides;
        var applied = new List<AppliedTag>();
        foreach (var tag in tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (tagOverrides.TryGetValue(tag.Id, out var on))
            {
                applied.Add(on
                    ? new AppliedTag(tag.Id, tag.Name, true, "operator-added", "added by the operator")
                    : new AppliedTag(tag.Id, tag.Name, false, "operator-removed", "removed by the operator"));
                continue;
            }

            var m = RuleTagMatcher.Match(tag, subject);
            applied.Add(new AppliedTag(tag.Id, tag.Name, m.Matched, m.Matched ? "matched" : "no-match", m.Reason));
        }

        var appliedNames = applied.Where(a => a.Applied).ToDictionary(a => a.TagId, a => a.Name, StringComparer.Ordinal);
        var choices = new List<RuleChoice>();
        var reaching = new List<AppliedRule>();
        foreach (var rule in rules.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase))
        {
            var via = rule.TagIds.Where(appliedNames.ContainsKey).Select(id => appliedNames[id]).Distinct().ToList();
            string source;
            bool reaches;
            if (!rule.Enabled)
            {
                (source, reaches) = ("disabled", false);
            }
            else if (ruleOverrides.TryGetValue(rule.Id, out var attached))
            {
                (source, reaches) = attached ? ("attached", true) : ("detached", false);
            }
            else
            {
                (source, reaches) = via.Count > 0 ? ("tag", true) : ("none", false);
            }

            choices.Add(new RuleChoice(rule.Id, rule.Title, rule.Text, rule.Enabled, reaches, source, via));
            if (reaches) reaching.Add(new AppliedRule(rule.Id, rule.Title, rule.Text, via, source == "attached"));
        }

        return new WorkItemRules(subject.WorkItemId, productId, applied, reaching, choices);
    }

    /// <summary>
    /// The subject for <paramref name="workItemId"/>. A workstream gives its
    /// repo, category and stage text (its work item's title and description,
    /// falling back to its name and summary). A standalone work item gives its
    /// repo, the <c>impl</c> category, and its title, description and tags.
    /// Null when neither exists; the product id is null when the repo is unknown.
    /// </summary>
    public static async Task<(RuleSubject Subject, string? ProductId)?> LoadSubjectAsync(ProductDbContext db, string workItemId, CancellationToken ct)
    {
        var ws = await db.Workstreams.AsNoTracking()
            .Where(w => w.Id == workItemId)
            .Select(w => new { w.Id, w.RepoId, w.Category, w.Name, w.Summary })
            .FirstOrDefaultAsync(ct);
        var wi = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == workItemId)
            .Select(w => new { w.RepoId, w.Title, w.DescriptionMd, w.TagsJson })
            .FirstOrDefaultAsync(ct);
        if (ws is null && wi is null) return null;

        RuleSubject subject;
        if (ws is not null)
        {
            var text = $"{wi?.Title ?? ws.Name}\n{(string.IsNullOrEmpty(wi?.DescriptionMd) ? ws.Summary : wi!.DescriptionMd)}";
            subject = new RuleSubject(ws.Id, ws.RepoId, string.IsNullOrEmpty(ws.Category) ? "impl" : ws.Category, text);
        }
        else
        {
            var itemTags = RuleCatalogJson.ReadList(wi!.TagsJson);
            var text = $"{wi.Title}\n{wi.DescriptionMd}" + (itemTags.Count > 0 ? $"\nTags: {string.Join(", ", itemTags)}" : string.Empty);
            subject = new RuleSubject(workItemId, wi.RepoId, "impl", text);
        }

        string? productId = null;
        if (!string.IsNullOrEmpty(subject.RepoId))
        {
            productId = await db.Repos.AsNoTracking()
                .Where(r => r.Id == subject.RepoId)
                .Select(r => r.ProductId)
                .FirstOrDefaultAsync(ct);
        }

        return (subject, productId);
    }

    /// <summary>
    /// Resolves the catalog for a work item execution or workstream. Null when
    /// <paramref name="workItemId"/> is neither (e.g. a plan doc) or its
    /// product is unknown.
    /// </summary>
    public static async Task<WorkItemRules?> ResolveAsync(ProductDbContext db, string workItemId, CancellationToken ct)
    {
        var loaded = await LoadSubjectAsync(db, workItemId, ct);
        if (loaded is not { ProductId: { Length: > 0 } productId } found) return null;

        var rules = (await db.CatalogRules.AsNoTracking().Where(r => r.ProductId == productId).ToListAsync(ct))
            .Select(RuleSpec.FromRow).ToList();
        var tags = (await db.RuleTags.AsNoTracking().Where(t => t.ProductId == productId).ToListAsync(ct))
            .Select(RuleTagSpec.FromRow).ToList();
        if (rules.Count == 0 && tags.Count == 0)
        {
            return new WorkItemRules(workItemId, productId, Array.Empty<AppliedTag>(), Array.Empty<AppliedRule>(), Array.Empty<RuleChoice>());
        }

        var tagOverrides = await db.WorkstreamTagOverrides.AsNoTracking()
            .Where(o => o.WorkstreamId == workItemId)
            .ToDictionaryAsync(o => o.TagId, o => o.On, ct);
        var ruleOverrides = await db.WorkItemRules.AsNoTracking()
            .Where(o => o.WorkItemId == workItemId)
            .ToDictionaryAsync(o => o.RuleId, o => o.On, ct);
        return Resolve(found.Subject, productId, tags, tagOverrides, rules, ruleOverrides);
    }

    /// <summary>The prompt block for the rules reaching a work item; empty when none do.</summary>
    public static string RenderPromptBlock(WorkItemRules? resolved)
    {
        if (resolved is null || resolved.Rules.Count == 0) return string.Empty;

        var reasons = resolved.Tags.Where(t => t.Applied).ToDictionary(t => t.Name, t => t.Reason, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
        sb.AppendLine("║  STANDING RULES -- PRODUCT RULE CATALOG, BINDING         ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine("The operator attached these rules from the product rule catalog,");
        sb.AppendLine("directly or through this work item's tags. Follow each one on every");
        sb.AppendLine("iteration; it overrides the plan, the evaluator and your own habits.");
        sb.AppendLine();
        for (var i = 0; i < resolved.Rules.Count; i++)
        {
            var rule = resolved.Rules[i];
            var why = rule.Attached
                ? "attached by the operator"
                : "tag " + string.Join("; ", rule.Via.Select(v => reasons.TryGetValue(v, out var r) ? $"{v}: {r}" : v));
            sb.AppendLine($"{i + 1}. {rule.Title}  [{why}]");
            sb.AppendLine(rule.Text.Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>JSON list helpers for the catalog's <c>*Json</c> columns.</summary>
public static class RuleCatalogJson
{
    public static IReadOnlyList<string> ReadList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    public static string WriteList(IEnumerable<string>? items) =>
        JsonSerializer.Serialize((items ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList());
}