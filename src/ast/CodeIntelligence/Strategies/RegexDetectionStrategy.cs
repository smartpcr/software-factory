// -----------------------------------------------------------------------
// <copyright file="RegexDetectionStrategy.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Strategies;

using System.Text.RegularExpressions;
using CodeIntelligence.Models;

/// <summary>
/// Detection strategy using regular expressions for fast pattern matching.
/// </summary>
public sealed class RegexDetectionStrategy : IDetectionStrategy
{
    private readonly Dictionary<string, Regex> compiledRegexCache = new();
    private bool disposed;

    /// <inheritdoc/>
    public string Name => "regex";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedLanguages => ["*"]; // All languages

    /// <inheritdoc/>
    public bool CanHandle(DetectionPattern pattern)
    {
        return pattern.Strategy.Equals("regex", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DetectedRelationship>> DetectAsync(
        DetectionRule rule,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var relationships = new List<DetectedRelationship>();

        foreach (var filePath in filePaths)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            foreach (var pattern in rule.Detection.Where(p => this.CanHandle(p)))
            {
                foreach (var patternDef in pattern.Patterns)
                {
                    if (!this.MatchesFilePattern(filePath, patternDef.FilePattern))
                    {
                        continue;
                    }

                    var detected = await this.ApplyPatternAsync(rule, patternDef, filePath, cancellationToken);
                    relationships.AddRange(detected);
                }
            }
        }

        return relationships;
    }

    private bool MatchesFilePattern(string filePath, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return true;
        }

        var fileName = Path.GetFileName(filePath);

        // Simple glob matching
        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/\\\\]*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(filePath, regex, RegexOptions.IgnoreCase) ||
               Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase);
    }

    private async Task<IEnumerable<DetectedRelationship>> ApplyPatternAsync(
        DetectionRule rule,
        PatternDefinition pattern,
        string filePath,
        CancellationToken cancellationToken)
    {
        var relationships = new List<DetectedRelationship>();

        if (string.IsNullOrEmpty(pattern.Regex))
        {
            return relationships;
        }

        try
        {
            var content = await File.ReadAllTextAsync(filePath, cancellationToken);
            var lines = content.Split('\n');

            var regex = this.GetOrCompileRegex(pattern.Regex);

            for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
            {
                var line = lines[lineNumber];
                var matches = regex.Matches(line);

                foreach (Match match in matches)
                {
                    var properties = this.ExtractProperties(match, pattern.Extract);

                    var relationship = this.CreateRelationship(
                        rule,
                        filePath,
                        lineNumber + 1,
                        match,
                        properties);

                    if (relationship != null)
                    {
                        relationships.Add(relationship);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Skip files that can't be read
        }

        return relationships;
    }

    private Regex GetOrCompileRegex(string pattern)
    {
        if (this.compiledRegexCache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        this.compiledRegexCache[pattern] = regex;
        return regex;
    }

    private Dictionary<string, object> ExtractProperties(
        Match match,
        IReadOnlyDictionary<string, string>? extractExpressions)
    {
        var properties = new Dictionary<string, object>
        {
            ["matchedText"] = match.Value
        };

        // Add named groups
        foreach (var groupName in match.Groups.Keys)
        {
            if (!int.TryParse(groupName, out _))
            {
                properties[groupName] = match.Groups[groupName].Value;
            }
        }

        // Add numbered groups
        for (var i = 1; i < match.Groups.Count; i++)
        {
            properties[$"group{i}"] = match.Groups[i].Value;
        }

        if (extractExpressions != null)
        {
            foreach (var (key, expression) in extractExpressions)
            {
                if (expression.StartsWith("$"))
                {
                    var groupNum = int.Parse(expression[1..]);
                    if (groupNum < match.Groups.Count)
                    {
                        properties[key] = match.Groups[groupNum].Value;
                    }
                }
                else if (match.Groups[expression].Success)
                {
                    properties[key] = match.Groups[expression].Value;
                }
            }
        }

        return properties;
    }

    private DetectedRelationship? CreateRelationship(
        DetectionRule rule,
        string filePath,
        int lineNumber,
        Match match,
        Dictionary<string, object> properties)
    {
        var sourceNode = new RelationshipNode
        {
            NodeType = rule.Output.SourceNodeType ?? rule.Output.NodeType ?? "Pattern",
            Name = match.Value.Length > 50 ? match.Value[..50] + "..." : match.Value,
            FullyQualifiedName = $"{filePath}:{lineNumber}",
            FilePath = filePath,
            LineNumber = lineNumber,
            ColumnNumber = match.Index + 1
        };

        var evidence = new Evidence
        {
            Source = "regex",
            Description = $"Matched regex pattern",
            Weight = rule.Confidence.BaseScore * 0.8f, // Regex matches get slightly lower confidence
            FilePath = filePath,
            LineNumber = lineNumber,
            MatchedText = match.Value
        };

        // Merge output properties
        foreach (var prop in rule.Output.Properties)
        {
            if (prop.Value != null)
            {
                properties[prop.Name] = prop.Value;
            }
        }

        return new DetectedRelationship
        {
            Id = Guid.NewGuid().ToString("N"),
            Category = rule.Category,
            RelationshipType = rule.RelationshipType,
            Source = sourceNode,
            Target = null,
            Confidence = rule.Confidence.BaseScore * 0.8f,
            EvidenceChain = [evidence],
            DetectionStrategy = this.Name,
            Properties = properties
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!this.disposed)
        {
            this.compiledRegexCache.Clear();
            this.disposed = true;
        }
    }
}
