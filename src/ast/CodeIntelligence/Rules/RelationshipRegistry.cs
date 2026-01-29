// -----------------------------------------------------------------------
// <copyright file="RelationshipRegistry.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Rules;

using CodeIntelligence.Models;

/// <summary>
/// Registry for managing detection rules.
/// </summary>
public sealed class RelationshipRegistry
{
    private readonly Dictionary<string, DetectionRule> rulesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DetectionRule>> rulesByCategory = new(StringComparer.OrdinalIgnoreCase);
    private readonly YamlRuleLoader loader = new();

    /// <summary>
    /// Gets all registered rules.
    /// </summary>
    public IReadOnlyList<DetectionRule> AllRules => this.rulesById.Values.ToList();

    /// <summary>
    /// Gets all rule categories.
    /// </summary>
    public IReadOnlyList<string> Categories => this.rulesByCategory.Keys.ToList();

    /// <summary>
    /// Registers a rule.
    /// </summary>
    /// <param name="rule">The rule to register.</param>
    public void RegisterRule(DetectionRule rule)
    {
        this.rulesById[rule.Id] = rule;

        if (!this.rulesByCategory.TryGetValue(rule.Category, out var categoryRules))
        {
            categoryRules = [];
            this.rulesByCategory[rule.Category] = categoryRules;
        }

        // Remove existing rule with same ID from category list
        categoryRules.RemoveAll(r => r.Id.Equals(rule.Id, StringComparison.OrdinalIgnoreCase));
        categoryRules.Add(rule);
    }

    /// <summary>
    /// Registers multiple rules.
    /// </summary>
    /// <param name="rules">The rules to register.</param>
    public void RegisterRules(IEnumerable<DetectionRule> rules)
    {
        foreach (var rule in rules)
        {
            this.RegisterRule(rule);
        }
    }

    /// <summary>
    /// Loads and registers rules from a YAML file.
    /// </summary>
    /// <param name="filePath">Path to the YAML file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var rules = await this.loader.LoadFromFileAsync(filePath, cancellationToken);
        this.RegisterRules(rules);
    }

    /// <summary>
    /// Loads and registers rules from a directory.
    /// </summary>
    /// <param name="directoryPath">Path to the directory.</param>
    /// <param name="recursive">Whether to search recursively.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task LoadFromDirectoryAsync(
        string directoryPath,
        bool recursive = true,
        CancellationToken cancellationToken = default)
    {
        var rules = await this.loader.LoadFromDirectoryAsync(directoryPath, recursive, cancellationToken);
        this.RegisterRules(rules);
    }

    /// <summary>
    /// Gets a rule by ID.
    /// </summary>
    /// <param name="id">The rule ID.</param>
    /// <returns>The rule, or null if not found.</returns>
    public DetectionRule? GetRule(string id)
    {
        return this.rulesById.TryGetValue(id, out var rule) ? rule : null;
    }

    /// <summary>
    /// Gets all rules in a category.
    /// </summary>
    /// <param name="category">The category name.</param>
    /// <returns>The rules in the category.</returns>
    public IReadOnlyList<DetectionRule> GetRulesByCategory(string category)
    {
        return this.rulesByCategory.TryGetValue(category, out var rules) ? rules : [];
    }

    /// <summary>
    /// Gets all enabled rules.
    /// </summary>
    /// <returns>All enabled rules.</returns>
    public IReadOnlyList<DetectionRule> GetEnabledRules()
    {
        return this.rulesById.Values.Where(r => r.Enabled).ToList();
    }

    /// <summary>
    /// Gets enabled rules by category.
    /// </summary>
    /// <param name="category">The category name.</param>
    /// <returns>The enabled rules in the category.</returns>
    public IReadOnlyList<DetectionRule> GetEnabledRulesByCategory(string category)
    {
        return this.GetRulesByCategory(category).Where(r => r.Enabled).ToList();
    }

    /// <summary>
    /// Gets rules ordered by priority.
    /// </summary>
    /// <returns>Rules ordered by priority (highest first).</returns>
    public IReadOnlyList<DetectionRule> GetRulesOrderedByPriority()
    {
        return this.rulesById.Values
            .Where(r => r.Enabled)
            .OrderByDescending(r => r.Priority)
            .ToList();
    }

    /// <summary>
    /// Gets rules by relationship type.
    /// </summary>
    /// <param name="relationshipType">The relationship type.</param>
    /// <returns>The matching rules.</returns>
    public IReadOnlyList<DetectionRule> GetRulesByRelationshipType(string relationshipType)
    {
        return this.rulesById.Values
            .Where(r => r.RelationshipType.Equals(relationshipType, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Removes a rule by ID.
    /// </summary>
    /// <param name="id">The rule ID.</param>
    /// <returns>True if the rule was removed.</returns>
    public bool RemoveRule(string id)
    {
        if (this.rulesById.TryGetValue(id, out var rule))
        {
            this.rulesById.Remove(id);

            if (this.rulesByCategory.TryGetValue(rule.Category, out var categoryRules))
            {
                categoryRules.RemoveAll(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Clears all registered rules.
    /// </summary>
    public void Clear()
    {
        this.rulesById.Clear();
        this.rulesByCategory.Clear();
    }

    /// <summary>
    /// Validates all registered rules.
    /// </summary>
    /// <returns>Validation errors, if any.</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        // Check for duplicate IDs
        var duplicates = this.rulesById.Values
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var dup in duplicates)
        {
            errors.Add($"Duplicate rule ID: {dup}");
        }

        // Check for empty detection patterns
        foreach (var rule in this.rulesById.Values)
        {
            if (rule.Detection.Count == 0)
            {
                errors.Add($"Rule {rule.Id} has no detection patterns");
            }

            if (rule.Output.Properties.Count == 0)
            {
                errors.Add($"Rule {rule.Id} has no output properties");
            }
        }

        return errors;
    }
}
