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
/// pattern applies to every workstream in its repos and categories. A tag
/// attaches policies (<see cref="RulePolicyRow.TagIdsJson"/>): written once,
/// they reach every matching work item and workstream of every story in the
/// product.
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
/// generator prompt of every iteration of every work item or workstream that
/// carries at least one of its tags, or that the operator attached it to, as
/// a binding operator directive. A rule with no tags reaches only the items it
/// is attached to.
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

    /// <summary>
    /// Unused since rules are grouped into policies (tags attach policies, not
    /// rules); always <c>[]</c>. Kept because the column already exists.
    /// </summary>
    public string TagIdsJson { get; set; } = "[]";

    /// <summary>A disabled rule stays in the catalog but reaches no prompt.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Phase 108 — a policy: a named group of catalog rules applied together. A
/// rule can be in any number of policies. A policy reaches a work item when
/// one of its tags applies there, or when the operator attaches it; every
/// enabled rule in it then goes into that item's prompt.
/// </summary>
public class RulePolicyRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Owning product (<c>products.Id</c>).</summary>
    public string ProductId { get; set; } = string.Empty;

    /// <summary>Display name, unique within the product, e.g. "Harness gate".</summary>
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>JSON <c>string[]</c> of <see cref="CatalogRuleRow.Id"/>, in the order the prompt lists them.</summary>
    public string RuleIdsJson { get; set; } = "[]";

    /// <summary>JSON <c>string[]</c> of <see cref="RuleTagRow.Id"/>; the policy is attached automatically where any applies.</summary>
    public string TagIdsJson { get; set; } = "[]";

    /// <summary>A disabled policy stays in the catalog but reaches no work item.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Phase 108 — the operator's decision on one policy for one work item or
/// workstream, which wins over the policy's tags: <see cref="On"/> true
/// attaches it, false detaches it. No row means the tags decide.
/// </summary>
public class WorkItemPolicyRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string WorkItemId { get; set; } = string.Empty;

    public string PolicyId { get; set; } = string.Empty;

    public bool On { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedBy { get; set; }
}

/// <summary>
/// Phase 108 — the operator's decision on one rule for one work item or
/// workstream, which wins over the rule's policies: <see cref="On"/> true
/// attaches the rule on its own, false keeps it out even when a policy that
/// contains it applies. No row means the policies decide.
/// </summary>
public class WorkItemRuleRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The work item or workstream (<c>work_items.Id</c> / <c>workstreams.Id</c>).</summary>
    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>The rule (<see cref="CatalogRuleRow.Id"/>).</summary>
    public string RuleId { get; set; } = string.Empty;

    public bool On { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedBy { get; set; }
}

/// <summary>
/// Phase 108 — the operator's decision on one tag for one work item or
/// workstream, which wins over the tag's matchers: <see cref="On"/> true adds
/// the tag even when nothing matches, false removes it even when something
/// does. No row means the matchers decide.
/// </summary>
public class WorkstreamTagOverrideRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The work item or workstream (<c>work_items.Id</c> / <c>workstreams.Id</c>).</summary>
    public string WorkstreamId { get; set; } = string.Empty;

    /// <summary>The tag (<see cref="RuleTagRow.Id"/>).</summary>
    public string TagId { get; set; } = string.Empty;

    public bool On { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedBy { get; set; }
}