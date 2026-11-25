// -----------------------------------------------------------------------
// <copyright file="RuleCatalogEntities.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

/// <summary>
/// Phase 108 — a tag in a product's rule catalog, such as "full harness gate".
/// A tag says which workstreams it applies to: the repos and workstream
/// categories it is limited to, and the phrases or regular expressions that
/// must appear in the workstream's stage text. A tag with no phrase and no
/// pattern applies to every workstream in its repos and categories. Rules are
/// attached to tags, never to workstreams directly, so a rule written once
/// reaches every matching workstream of every story in the product.
/// </summary>
public class RuleTagRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Owning product (<c>products.Id</c>).</summary>
    public string ProductId { get; set; } = string.Empty;

    /// <summary>Display name, unique within the product, e.g. "full harness gate".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What the tag means, shown to the operator.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>JSON <c>string[]</c> of repo ids the tag is limited to; empty = every repo.</summary>
    public string RepoIdsJson { get; set; } = "[]";

    /// <summary>JSON <c>string[]</c> of workstream categories (<c>impl</c>, <c>qa-e2e</c>); empty = every category.</summary>
    public string CategoriesJson { get; set; } = "[]";

    /// <summary>JSON <c>string[]</c> of phrases matched case-insensitively against the stage text; any one is enough.</summary>
    public string PhrasesJson { get; set; } = "[]";

    /// <summary>JSON <c>string[]</c> of case-insensitive regular expressions over the stage text; any one is enough.</summary>
    public string PatternsJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Phase 108 — a standing rule in a product's rule catalog. It goes into the
/// generator prompt of every iteration of every workstream that carries at
/// least one of its tags, as a binding operator directive.
/// </summary>
public class CatalogRuleRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Owning product (<c>products.Id</c>).</summary>
    public string ProductId { get; set; } = string.Empty;

    /// <summary>Short name shown in the catalog and in the prompt heading.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The directive the agent receives.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>JSON <c>string[]</c> of <see cref="RuleTagRow.Id"/>; the rule applies where any of them applies.</summary>
    public string TagIdsJson { get; set; } = "[]";

    /// <summary>A disabled rule stays in the catalog but reaches no prompt.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Phase 108 — the operator's decision on one tag for one workstream, which
/// wins over the tag's matchers: <see cref="On"/> true adds the tag even when
/// nothing matches, false removes it even when something does. No row means
/// the matchers decide.
/// </summary>
public class WorkstreamTagOverrideRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The workstream (<c>workstreams.Id</c>).</summary>
    public string WorkstreamId { get; set; } = string.Empty;

    /// <summary>The tag (<see cref="RuleTagRow.Id"/>).</summary>
    public string TagId { get; set; } = string.Empty;

    public bool On { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedBy { get; set; }
}