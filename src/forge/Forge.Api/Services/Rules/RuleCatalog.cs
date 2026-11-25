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
/// What a tag is matched against: the workstream's repo and category, and its
/// stage text (the work item title and description, which carry the plan
/// stage's steps and test scenarios).
/// </summary>
public sealed record RuleSubject(string WorkstreamId, string? RepoId, string Category, string Text);

/// <summary>The matcher's verdict on one tag, with a reason the operator can read.</summary>
public sealed record TagMatch(bool Matched, string Reason);

/// <summary>
/// How one tag stands on one workstream. <see cref="Source"/> is
/// <c>matched</c> or <c>no-match</c> when the matchers decided, and
/// <c>operator-added</c> or <c>operator-removed</c> when an override did.
/// </summary>
public sealed record AppliedTag(string TagId, string Name, bool Applied, string Source, string Reason);

/// <summary>A rule that reaches a workstream, with the names of the tags that brought it.</summary>
public sealed record AppliedRule(string Id, string Title, string Text, IReadOnlyList<string> Via);

/// <summary>Every catalog tag's standing on a workstream, and the rules that reach it.</summary>
public sealed record WorkstreamRules(string WorkstreamId, string ProductId, IReadOnlyList<AppliedTag> Tags, IReadOnlyList<AppliedRule> Rules);

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
/// Resolves which catalog tags and rules reach a workstream, and renders the
/// rules as the prompt block the generator sees on every iteration.
/// </summary>
public static class RuleCatalogResolver
{
    /// <summary>Pure resolution: overrides win over matchers; enabled rules with an applied tag reach the workstream.</summary>
    public static WorkstreamRules Resolve(
        RuleSubject subject,
        string productId,
        IEnumerable<RuleTagSpec> tags,
        IReadOnlyDictionary<string, bool> overrides,
        IEnumerable<RuleSpec> rules)
    {
        var applied = new List<AppliedTag>();
        foreach (var tag in tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (overrides.TryGetValue(tag.Id, out var on))
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
        var reaching = new List<AppliedRule>();
        foreach (var rule in rules.Where(r => r.Enabled))
        {
            var via = rule.TagIds.Where(appliedNames.ContainsKey).Select(id => appliedNames[id]).Distinct().ToList();
            if (via.Count > 0) reaching.Add(new AppliedRule(rule.Id, rule.Title, rule.Text, via));
        }

        return new WorkstreamRules(subject.WorkstreamId, productId, applied, reaching.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// The subject for <paramref name="workstreamId"/>: repo, category, and the
    /// work item's title and description (falling back to the workstream's
    /// name and summary). Null when the workstream does not exist.
    /// </summary>
    public static async Task<(RuleSubject Subject, string? ProductId)?> LoadSubjectAsync(ProductDbContext db, string workstreamId, CancellationToken ct)
    {
        var ws = await db.Workstreams.AsNoTracking()
            .Where(w => w.Id == workstreamId)
            .Select(w => new { w.Id, w.RepoId, w.Category, w.Name, w.Summary })
            .FirstOrDefaultAsync(ct);
        if (ws is null) return null;

        var wi = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == workstreamId)
            .Select(w => new { w.Title, w.DescriptionMd })
            .FirstOrDefaultAsync(ct);
        var text = $"{wi?.Title ?? ws.Name}\n{(string.IsNullOrEmpty(wi?.DescriptionMd) ? ws.Summary : wi!.DescriptionMd)}";

        string? productId = null;
        if (!string.IsNullOrEmpty(ws.RepoId))
        {
            productId = await db.Repos.AsNoTracking()
                .Where(r => r.Id == ws.RepoId)
                .Select(r => r.ProductId)
                .FirstOrDefaultAsync(ct);
        }

        return (new RuleSubject(ws.Id, ws.RepoId, string.IsNullOrEmpty(ws.Category) ? "impl" : ws.Category, text), productId);
    }

    /// <summary>
    /// Resolves the catalog for <paramref name="workstreamId"/>. Null when the
    /// work item is not a workstream (a plan doc) or its product is unknown.
    /// </summary>
    public static async Task<WorkstreamRules?> ResolveAsync(ProductDbContext db, string workstreamId, CancellationToken ct)
    {
        var loaded = await LoadSubjectAsync(db, workstreamId, ct);
        if (loaded is not { ProductId: { Length: > 0 } productId } found) return null;

        var tags = (await db.RuleTags.AsNoTracking().Where(t => t.ProductId == productId).ToListAsync(ct))
            .Select(RuleTagSpec.FromRow).ToList();
        if (tags.Count == 0) return new WorkstreamRules(workstreamId, productId, Array.Empty<AppliedTag>(), Array.Empty<AppliedRule>());

        var overrides = await db.WorkstreamTagOverrides.AsNoTracking()
            .Where(o => o.WorkstreamId == workstreamId)
            .ToDictionaryAsync(o => o.TagId, o => o.On, ct);
        var rules = (await db.CatalogRules.AsNoTracking().Where(r => r.ProductId == productId).ToListAsync(ct))
            .Select(RuleSpec.FromRow);
        return Resolve(found.Subject, productId, tags, overrides, rules);
    }

    /// <summary>The prompt block for the rules reaching a workstream; empty when none do.</summary>
    public static string RenderPromptBlock(WorkstreamRules? resolved)
    {
        if (resolved is null || resolved.Rules.Count == 0) return string.Empty;

        var reasons = resolved.Tags.Where(t => t.Applied).ToDictionary(t => t.Name, t => t.Reason, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
        sb.AppendLine("║  STANDING RULES -- PRODUCT RULE CATALOG, BINDING         ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine("The operator's rule catalog attaches these rules to this workstream");
        sb.AppendLine("through its tags. Follow each one on every iteration; it overrides");
        sb.AppendLine("the plan, the evaluator and your own habits.");
        sb.AppendLine();
        for (var i = 0; i < resolved.Rules.Count; i++)
        {
            var rule = resolved.Rules[i];
            var why = string.Join("; ", rule.Via.Select(v => reasons.TryGetValue(v, out var r) ? $"{v}: {r}" : v));
            sb.AppendLine($"{i + 1}. {rule.Title}  [tag {why}]");
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