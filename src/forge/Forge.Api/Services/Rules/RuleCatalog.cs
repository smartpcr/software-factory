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
public sealed record RuleSpec(string Id, string Title, string Text, bool Enabled)
{
    public static RuleSpec FromRow(CatalogRuleRow row) => new(row.Id, row.Title, row.Text, row.Enabled);
}

/// <summary>A policy as the resolver sees it: its rules in prompt order and the tags that attach it.</summary>
public sealed record PolicySpec(string Id, string Name, IReadOnlyList<string> RuleIds, IReadOnlyList<string> TagIds, bool Enabled)
{
    public static PolicySpec FromRow(RulePolicyRow row) => new(
        row.Id, row.Name, RuleCatalogJson.ReadList(row.RuleIdsJson), RuleCatalogJson.ReadList(row.TagIdsJson), row.Enabled);
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
/// How one tag stands on one work item. <see cref="Source"/> is
/// <c>matched</c> or <c>no-match</c> when the matchers decided, and
/// <c>operator-added</c> or <c>operator-removed</c> when an override did.
/// </summary>
public sealed record AppliedTag(string TagId, string Name, bool Applied, string Source, string Reason);

/// <summary>
/// How one policy stands on a work item. <see cref="Source"/> is <c>tag</c>
/// (one of its tags applies, named in <see cref="Via"/>), <c>attached</c> /
/// <c>detached</c> (the operator's choice for this item), <c>none</c> or
/// <c>disabled</c>.
/// </summary>
public sealed record PolicyChoice(string Id, string Name, bool Enabled, bool Applied, string Source, IReadOnlyList<string> Via, int RuleCount);

/// <summary>
/// How one rule stands on a work item. <see cref="Source"/> is <c>policy</c>
/// (an applied policy contains it, named in <see cref="Via"/>), <c>attached</c>
/// (picked on its own) / <c>detached</c> (kept out here), <c>none</c> or
/// <c>disabled</c>. <see cref="Policies"/> names every policy that contains it.
/// </summary>
public sealed record RuleChoice(
    string Id, string Title, string Text, bool Enabled, bool Applied, string Source,
    IReadOnlyList<string> Via, IReadOnlyList<string> Policies);

/// <summary>A rule in the next prompt, with why: the applied policies that contain it, or picked on its own.</summary>
public sealed record AppliedRule(string Id, string Title, string Text, IReadOnlyList<string> Via, bool Attached);

/// <summary>Every tag's, policy's and rule's standing on a work item, and the rules in its next prompt.</summary>
public sealed record WorkItemRules(
    string WorkItemId,
    string ProductId,
    IReadOnlyList<AppliedTag> Tags,
    IReadOnlyList<PolicyChoice> Policies,
    IReadOnlyList<RuleChoice> Choices,
    IReadOnlyList<AppliedRule> Rules);

/// <summary>Decides whether a catalog tag applies to a work item.</summary>
public static class RuleTagMatcher
{
    /// <summary>Bound on one pattern's evaluation, so an operator's regex can never stall a prompt build.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private const int MaxQuoted = 3;

    /// <summary>
    /// A tag applies when the work item is in one of its repos and categories
    /// (an empty list allows all) and its text contains one of the phrases or
    /// matches one of the patterns, case-insensitively. A tag with neither
    /// phrases nor patterns applies to every work item it allows.
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
/// Resolves which policies and rules reach a work item execution or a
/// workstream, and renders the rules as the prompt block the generator sees on
/// every iteration. Precedence, most specific first: the operator's choice on a
/// rule for this item, then on a policy for this item, then on a tag for this
/// item, then the tag's matchers.
/// </summary>
public static class RuleCatalogResolver
{
    private static readonly IReadOnlyDictionary<string, bool> NoOverrides = new Dictionary<string, bool>();

    /// <summary>Pure resolution over one work item's subject, the catalog and the item's overrides.</summary>
    public static WorkItemRules Resolve(
        RuleSubject subject,
        string productId,
        IEnumerable<RuleTagSpec> tags,
        IEnumerable<PolicySpec> policies,
        IEnumerable<RuleSpec> rules,
        IReadOnlyDictionary<string, bool>? tagOverrides = null,
        IReadOnlyDictionary<string, bool>? policyOverrides = null,
        IReadOnlyDictionary<string, bool>? ruleOverrides = null)
    {
        tagOverrides ??= NoOverrides;
        policyOverrides ??= NoOverrides;
        ruleOverrides ??= NoOverrides;

        var appliedTags = new List<AppliedTag>();
        foreach (var tag in tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (tagOverrides.TryGetValue(tag.Id, out var on))
            {
                appliedTags.Add(on
                    ? new AppliedTag(tag.Id, tag.Name, true, "operator-added", "added by the operator")
                    : new AppliedTag(tag.Id, tag.Name, false, "operator-removed", "removed by the operator"));
                continue;
            }

            var m = RuleTagMatcher.Match(tag, subject);
            appliedTags.Add(new AppliedTag(tag.Id, tag.Name, m.Matched, m.Matched ? "matched" : "no-match", m.Reason));
        }

        var tagNames = appliedTags.Where(a => a.Applied).ToDictionary(a => a.TagId, a => a.Name, StringComparer.Ordinal);
        var policyList = policies.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var policyChoices = new List<PolicyChoice>();
        foreach (var policy in policyList)
        {
            var via = policy.TagIds.Where(tagNames.ContainsKey).Select(id => tagNames[id]).Distinct().ToList();
            var (source, applies) = Decide(policy.Enabled, policyOverrides, policy.Id, via.Count > 0, "tag");
            policyChoices.Add(new PolicyChoice(policy.Id, policy.Name, policy.Enabled, applies, source, via, policy.RuleIds.Count));
        }

        var appliedPolicies = policyChoices.Where(p => p.Applied).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var ruleList = rules.ToList();
        var ruleOrder = OrderRules(ruleList, policyList.Where(p => appliedPolicies.Contains(p.Id)));
        var ruleChoices = new List<RuleChoice>();
        var reaching = new List<AppliedRule>();
        foreach (var rule in ruleOrder)
        {
            var containing = policyList.Where(p => p.RuleIds.Contains(rule.Id)).ToList();
            var via = containing.Where(p => appliedPolicies.Contains(p.Id)).Select(p => p.Name).ToList();
            var (source, applies) = Decide(rule.Enabled, ruleOverrides, rule.Id, via.Count > 0, "policy");
            ruleChoices.Add(new RuleChoice(rule.Id, rule.Title, rule.Text, rule.Enabled, applies, source, via, containing.Select(p => p.Name).ToList()));
            if (applies) reaching.Add(new AppliedRule(rule.Id, rule.Title, rule.Text, via, source == "attached"));
        }

        return new WorkItemRules(subject.WorkItemId, productId, appliedTags, policyChoices, ruleChoices, reaching);
    }

    /// <summary>Disabled loses; then the operator's choice for this item; then whether something above brings it.</summary>
    private static (string Source, bool Applies) Decide(
        bool enabled, IReadOnlyDictionary<string, bool> overrides, string id, bool brought, string broughtBy)
    {
        if (!enabled) return ("disabled", false);
        if (overrides.TryGetValue(id, out var on)) return on ? ("attached", true) : ("detached", false);
        return brought ? (broughtBy, true) : ("none", false);
    }

    /// <summary>Rules of applied policies first, in policy order, then the rest by title.</summary>
    private static List<RuleSpec> OrderRules(List<RuleSpec> rules, IEnumerable<PolicySpec> appliedPolicies)
    {
        var byId = rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var ordered = new List<RuleSpec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in appliedPolicies.SelectMany(p => p.RuleIds))
        {
            if (byId.TryGetValue(id, out var r) && seen.Add(id)) ordered.Add(r);
        }

        ordered.AddRange(rules.Where(r => !seen.Contains(r.Id)).OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase));
        return ordered;
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
        var policies = (await db.RulePolicies.AsNoTracking().Where(p => p.ProductId == productId).ToListAsync(ct))
            .Select(PolicySpec.FromRow).ToList();
        var tags = (await db.RuleTags.AsNoTracking().Where(t => t.ProductId == productId).ToListAsync(ct))
            .Select(RuleTagSpec.FromRow).ToList();
        if (rules.Count == 0)
        {
            return new WorkItemRules(workItemId, productId, Array.Empty<AppliedTag>(), Array.Empty<PolicyChoice>(), Array.Empty<RuleChoice>(), Array.Empty<AppliedRule>());
        }

        var tagOverrides = await db.WorkstreamTagOverrides.AsNoTracking()
            .Where(o => o.WorkstreamId == workItemId)
            .ToDictionaryAsync(o => o.TagId, o => o.On, ct);
        var policyOverrides = await db.WorkItemPolicies.AsNoTracking()
            .Where(o => o.WorkItemId == workItemId)
            .ToDictionaryAsync(o => o.PolicyId, o => o.On, ct);
        var ruleOverrides = await db.WorkItemRules.AsNoTracking()
            .Where(o => o.WorkItemId == workItemId)
            .ToDictionaryAsync(o => o.RuleId, o => o.On, ct);
        return Resolve(found.Subject, productId, tags, policies, rules, tagOverrides, policyOverrides, ruleOverrides);
    }

    /// <summary>The prompt block for the rules reaching a work item; empty when none do.</summary>
    public static string RenderPromptBlock(WorkItemRules? resolved)
    {
        if (resolved is null || resolved.Rules.Count == 0) return string.Empty;

        var policyWhy = resolved.Policies.Where(p => p.Applied).ToDictionary(
            p => p.Name,
            p => p.Source == "attached" ? "attached by the operator" : $"tag {string.Join(", ", p.Via)}",
            StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
        sb.AppendLine("║  STANDING RULES -- PRODUCT POLICIES, BINDING             ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine("The operator applied these rules to this work item, through its");
        sb.AppendLine("policies or one by one. Follow each one on every iteration; it");
        sb.AppendLine("overrides the plan, the evaluator and your own habits.");
        sb.AppendLine();
        for (var i = 0; i < resolved.Rules.Count; i++)
        {
            var rule = resolved.Rules[i];
            var why = rule.Attached
                ? "attached by the operator"
                : "policy " + string.Join("; ", rule.Via.Select(v => policyWhy.TryGetValue(v, out var w) ? $"{v} ({w})" : v));
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