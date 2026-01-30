// -----------------------------------------------------------------------
// <copyright file="YamlRuleLoader.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Rules;

using CodeIntelligence.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Loads detection rules from YAML files.
/// </summary>
public sealed class YamlRuleLoader
{
    private static readonly EnumerationOptions SafeEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
    };

    private readonly IDeserializer deserializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="YamlRuleLoader"/> class.
    /// </summary>
    public YamlRuleLoader()
    {
        this.deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <summary>
    /// Loads rules from a YAML file.
    /// </summary>
    /// <param name="filePath">Path to the YAML file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loaded rules.</returns>
    public async Task<IReadOnlyList<DetectionRule>> LoadFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        return this.LoadFromYaml(content);
    }

    /// <summary>
    /// Loads rules from a YAML string.
    /// </summary>
    /// <param name="yaml">The YAML content.</param>
    /// <returns>The loaded rules.</returns>
    public IReadOnlyList<DetectionRule> LoadFromYaml(string yaml)
    {
        var ruleFile = this.deserializer.Deserialize<RuleFileDto>(yaml);
        return this.ConvertToRules(ruleFile);
    }

    /// <summary>
    /// Loads rules from multiple YAML files.
    /// </summary>
    /// <param name="filePaths">Paths to the YAML files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All loaded rules.</returns>
    public async Task<IReadOnlyList<DetectionRule>> LoadFromFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var allRules = new List<DetectionRule>();

        foreach (var filePath in filePaths)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var rules = await this.LoadFromFileAsync(filePath, cancellationToken);
                allRules.AddRange(rules);
            }
            catch (Exception ex)
            {
                throw new RuleLoadException($"Failed to load rules from {filePath}", ex);
            }
        }

        return allRules;
    }

    /// <summary>
    /// Loads all rule files from a directory.
    /// </summary>
    /// <param name="directoryPath">Path to the directory.</param>
    /// <param name="recursive">Whether to search recursively.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All loaded rules.</returns>
    public async Task<IReadOnlyList<DetectionRule>> LoadFromDirectoryAsync(
        string directoryPath,
        bool recursive = true,
        CancellationToken cancellationToken = default)
    {
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
        };
        var files = Directory.GetFiles(directoryPath, "*.yaml", enumOptions)
            .Concat(Directory.GetFiles(directoryPath, "*.yml", enumOptions));

        return await this.LoadFromFilesAsync(files, cancellationToken);
    }

    private IReadOnlyList<DetectionRule> ConvertToRules(RuleFileDto ruleFile)
    {
        if (ruleFile.Rules == null || ruleFile.Rules.Count == 0)
        {
            return [];
        }

        var rules = new List<DetectionRule>();

        foreach (var ruleDto in ruleFile.Rules)
        {
            var rule = this.ConvertRule(ruleDto);
            if (rule != null)
            {
                rules.Add(rule);
            }
        }

        return rules;
    }

    private DetectionRule? ConvertRule(RuleDto dto)
    {
        if (string.IsNullOrEmpty(dto.Id) ||
            string.IsNullOrEmpty(dto.Category) ||
            string.IsNullOrEmpty(dto.RelationshipType))
        {
            return null;
        }

        var detection = dto.Detection?.Select(this.ConvertDetectionPattern).ToList()
            ?? [];

        var output = dto.Output != null
            ? this.ConvertOutputSpec(dto.Output)
            : new OutputSpecification { Properties = [] };

        var confidence = dto.Confidence != null
            ? this.ConvertConfidenceSpec(dto.Confidence)
            : new ConfidenceSpecification();

        return new DetectionRule
        {
            Id = dto.Id,
            Name = dto.Name ?? dto.Id,
            Category = dto.Category,
            RelationshipType = dto.RelationshipType,
            Enabled = dto.Enabled ?? true,
            Priority = dto.Priority ?? 50,
            Detection = detection,
            Validation = dto.Validation != null ? this.ConvertValidation(dto.Validation) : null,
            Output = output,
            Confidence = confidence
        };
    }

    private DetectionPattern ConvertDetectionPattern(DetectionPatternDto dto)
    {
        var patterns = dto.Patterns?.Select(this.ConvertPatternDef).ToList() ?? [];

        return new DetectionPattern
        {
            Strategy = dto.Strategy ?? "ast",
            Language = dto.Language,
            Patterns = patterns
        };
    }

    private PatternDefinition ConvertPatternDef(PatternDefinitionDto dto)
    {
        return new PatternDefinition
        {
            NodeType = dto.NodeType,
            Condition = dto.Condition,
            Extract = dto.Extract,
            FilePattern = dto.FilePattern,
            Regex = dto.Regex,
            XPath = dto.XPath,
            JsonPath = dto.JsonPath
        };
    }

    private ValidationCriteria ConvertValidation(ValidationDto dto)
    {
        var methods = dto.Methods?.Select(m => new MethodSignature
        {
            Name = m.Name ?? string.Empty,
            ReturnType = m.ReturnType,
            Required = m.Required ?? true
        }).ToList();

        return new ValidationCriteria
        {
            Interface = dto.Interface,
            BaseClass = dto.BaseClass,
            Attributes = dto.Attributes,
            Methods = methods
        };
    }

    private OutputSpecification ConvertOutputSpec(OutputDto dto)
    {
        var properties = dto.Properties?.Select(p => new PropertySpecification
        {
            Name = p.Name ?? string.Empty,
            Value = p.Value,
            Derive = p.Derive
        }).ToList() ?? [];

        return new OutputSpecification
        {
            SourceNodeType = dto.SourceNodeType,
            TargetNodeType = dto.TargetNodeType,
            NodeType = dto.NodeType,
            EdgeType = dto.EdgeType,
            Properties = properties
        };
    }

    private ConfidenceSpecification ConvertConfidenceSpec(ConfidenceDto dto)
    {
        return new ConfidenceSpecification
        {
            BaseScore = dto.BaseScore ?? 0.5f,
            EvidenceWeights = dto.EvidenceWeights,
            MinimumThreshold = dto.MinimumThreshold ?? 0.7f
        };
    }

    #region DTOs for YAML deserialization

    private sealed class RuleFileDto
    {
        public List<RuleDto>? Rules { get; set; }
    }

    private sealed class RuleDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? RelationshipType { get; set; }
        public bool? Enabled { get; set; }
        public int? Priority { get; set; }
        public List<DetectionPatternDto>? Detection { get; set; }
        public ValidationDto? Validation { get; set; }
        public OutputDto? Output { get; set; }
        public ConfidenceDto? Confidence { get; set; }
    }

    private sealed class DetectionPatternDto
    {
        public string? Strategy { get; set; }
        public string? Language { get; set; }
        public List<PatternDefinitionDto>? Patterns { get; set; }
    }

    private sealed class PatternDefinitionDto
    {
        public string? NodeType { get; set; }
        public string? Condition { get; set; }
        public Dictionary<string, string>? Extract { get; set; }
        public string? FilePattern { get; set; }
        public string? Regex { get; set; }
        public string? XPath { get; set; }
        public string? JsonPath { get; set; }
    }

    private sealed class ValidationDto
    {
        public string? Interface { get; set; }
        public string? BaseClass { get; set; }
        public List<string>? Attributes { get; set; }
        public List<MethodDto>? Methods { get; set; }
    }

    private sealed class MethodDto
    {
        public string? Name { get; set; }
        public string? ReturnType { get; set; }
        public bool? Required { get; set; }
    }

    private sealed class OutputDto
    {
        public string? SourceNodeType { get; set; }
        public string? TargetNodeType { get; set; }
        public string? NodeType { get; set; }
        public string? EdgeType { get; set; }
        public List<PropertyDto>? Properties { get; set; }
    }

    private sealed class PropertyDto
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
        public string? Derive { get; set; }
    }

    private sealed class ConfidenceDto
    {
        public float? BaseScore { get; set; }
        public Dictionary<string, float>? EvidenceWeights { get; set; }
        public float? MinimumThreshold { get; set; }
    }

    #endregion
}

/// <summary>
/// Exception thrown when rule loading fails.
/// </summary>
public sealed class RuleLoadException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RuleLoadException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public RuleLoadException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
