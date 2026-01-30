// -----------------------------------------------------------------------
// <copyright file="ConfigFileDetectionStrategy.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Strategies;

using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using CodeIntelligence.Models;

/// <summary>
/// Detection strategy for XML, JSON, and YAML configuration files.
/// </summary>
public sealed class ConfigFileDetectionStrategy : IDetectionStrategy
{
    private bool disposed;

    /// <inheritdoc/>
    public string Name => "config";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedLanguages => ["xml", "json", "yaml", "yml"];

    /// <inheritdoc/>
    public bool CanHandle(DetectionPattern pattern)
    {
        return pattern.Strategy.Equals("config", StringComparison.OrdinalIgnoreCase);
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
            return this.IsSupportedConfigFile(filePath);
        }

        var fileName = Path.GetFileName(filePath);

        // Simple glob matching
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/\\\\]*")
            .Replace("\\?", ".") + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(filePath, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
               System.Text.RegularExpressions.Regex.IsMatch(fileName, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private bool IsSupportedConfigFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".xml" or ".json" or ".yaml" or ".yml" or ".config" or ".csproj" or ".props";
    }

    private async Task<IEnumerable<DetectedRelationship>> ApplyPatternAsync(
        DetectionRule rule,
        PatternDefinition pattern,
        string filePath,
        CancellationToken cancellationToken)
    {
        var relationships = new List<DetectedRelationship>();
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        try
        {
            if (ext is ".xml" or ".config" or ".csproj" or ".props")
            {
                var xmlRelationships = await this.ApplyXmlPatternAsync(rule, pattern, filePath, cancellationToken);
                relationships.AddRange(xmlRelationships);
            }
            else if (ext == ".json")
            {
                var jsonRelationships = await this.ApplyJsonPatternAsync(rule, pattern, filePath, cancellationToken);
                relationships.AddRange(jsonRelationships);
            }
        }
        catch (Exception)
        {
            // Skip files that can't be parsed
        }

        return relationships;
    }

    private async Task<IEnumerable<DetectedRelationship>> ApplyXmlPatternAsync(
        DetectionRule rule,
        PatternDefinition pattern,
        string filePath,
        CancellationToken cancellationToken)
    {
        var relationships = new List<DetectedRelationship>();

        if (string.IsNullOrEmpty(pattern.XPath))
        {
            return relationships;
        }

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var doc = XDocument.Parse(content);

        // Handle namespaces in XPath
        var nsManager = new System.Xml.XmlNamespaceManager(new System.Xml.NameTable());

        // Add common namespaces
        if (doc.Root?.Name.Namespace != null && !string.IsNullOrEmpty(doc.Root.Name.Namespace.NamespaceName))
        {
            nsManager.AddNamespace("ns", doc.Root.Name.Namespace.NamespaceName);
        }

        var elements = doc.XPathSelectElements(pattern.XPath, nsManager);

        foreach (var element in elements)
        {
            var properties = this.ExtractXmlProperties(element, pattern.Extract);

            var relationship = this.CreateRelationship(rule, filePath, element.Name.LocalName, properties);
            if (relationship != null)
            {
                relationships.Add(relationship);
            }
        }

        return relationships;
    }

    private Dictionary<string, object> ExtractXmlProperties(
        XElement element,
        IReadOnlyDictionary<string, string>? extractExpressions)
    {
        var properties = new Dictionary<string, object>
        {
            ["elementName"] = element.Name.LocalName,
            ["elementValue"] = element.Value
        };

        // Add all attributes
        foreach (var attr in element.Attributes())
        {
            properties[$"@{attr.Name.LocalName}"] = attr.Value;
        }

        if (extractExpressions != null)
        {
            foreach (var (key, expression) in extractExpressions)
            {
                if (expression.StartsWith("@"))
                {
                    var attrName = expression[1..];
                    var attrValue = element.Attribute(attrName)?.Value;
                    if (attrValue != null)
                    {
                        properties[key] = attrValue;
                    }
                }
                else if (expression.StartsWith("./"))
                {
                    var childElement = element.XPathSelectElement(expression);
                    if (childElement != null)
                    {
                        properties[key] = childElement.Value;
                    }
                }
            }
        }

        return properties;
    }

    private async Task<IEnumerable<DetectedRelationship>> ApplyJsonPatternAsync(
        DetectionRule rule,
        PatternDefinition pattern,
        string filePath,
        CancellationToken cancellationToken)
    {
        var relationships = new List<DetectedRelationship>();

        if (string.IsNullOrEmpty(pattern.JsonPath))
        {
            return relationships;
        }

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);

        using var doc = JsonDocument.Parse(content);

        // Simple JSON path implementation for common patterns
        var elements = this.SelectJsonElements(doc.RootElement, pattern.JsonPath);

        foreach (var element in elements)
        {
            var properties = this.ExtractJsonProperties(element, pattern.Extract);

            var relationship = this.CreateRelationship(rule, filePath, pattern.JsonPath, properties);
            if (relationship != null)
            {
                relationships.Add(relationship);
            }
        }

        return relationships;
    }

    private IEnumerable<JsonElement> SelectJsonElements(JsonElement root, string jsonPath)
    {
        // Simple JSON path support for common patterns like "$.connections[*]"
        var parts = jsonPath.TrimStart('$', '.').Split('.');
        var current = new List<JsonElement> { root };

        foreach (var part in parts)
        {
            var next = new List<JsonElement>();

            foreach (var element in current)
            {
                if (part.EndsWith("[*]"))
                {
                    var propName = part[..^3];
                    if (element.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.Array)
                    {
                        next.AddRange(prop.EnumerateArray());
                    }
                }
                else if (element.TryGetProperty(part, out var prop))
                {
                    next.Add(prop);
                }
            }

            current = next;
        }

        return current;
    }

    private Dictionary<string, object> ExtractJsonProperties(
        JsonElement element,
        IReadOnlyDictionary<string, string>? extractExpressions)
    {
        var properties = new Dictionary<string, object>();

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                properties[prop.Name] = prop.Value.ToString();
            }
        }
        else
        {
            properties["value"] = element.ToString();
        }

        return properties;
    }

    private DetectedRelationship? CreateRelationship(
        DetectionRule rule,
        string filePath,
        string elementName,
        Dictionary<string, object> properties)
    {
        var sourceNode = new RelationshipNode
        {
            NodeType = rule.Output.SourceNodeType ?? rule.Output.NodeType ?? "ConfigElement",
            Name = elementName,
            FullyQualifiedName = $"{filePath}:{elementName}",
            FilePath = filePath,
            LineNumber = 1,
            ColumnNumber = 1
        };

        var evidence = new Evidence
        {
            Source = "config",
            Description = $"Found in configuration file",
            Weight = rule.Confidence.BaseScore,
            FilePath = filePath
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
            Confidence = rule.Confidence.BaseScore,
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
            this.disposed = true;
        }
    }
}
