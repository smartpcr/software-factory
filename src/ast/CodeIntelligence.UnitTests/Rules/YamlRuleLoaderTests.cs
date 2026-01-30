// -----------------------------------------------------------------------
// <copyright file="YamlRuleLoaderTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Rules;

using CodeIntelligence.Rules;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="YamlRuleLoader"/>.
/// </summary>
[TestClass]
public class YamlRuleLoaderTests
{
    private YamlRuleLoader loader = null!;

    [TestInitialize]
    public void Setup()
    {
        this.loader = new YamlRuleLoader();
    }

    [TestMethod]
    public void LoadFromYaml_ValidRule_ParsesSuccessfully()
    {
        // Arrange
        var yaml = @"
rules:
  - id: test-rule
    name: Test Rule
    category: structural
    relationshipType: Inheritance
    enabled: true
    priority: 100
    detection:
      - strategy: ast
        language: csharp
        patterns:
          - nodeType: ClassDeclaration
            condition: ""inheritsFrom('Base')""
    output:
      sourceNodeType: Class
      targetNodeType: Class
      edgeType: InheritsFrom
      properties:
        - name: className
    confidence:
      baseScore: 0.95
      minimumThreshold: 0.90
";

        // Act
        var rules = this.loader.LoadFromYaml(yaml);

        // Assert
        rules.Should().HaveCount(1);
        var rule = rules[0];
        rule.Id.Should().Be("test-rule");
        rule.Name.Should().Be("Test Rule");
        rule.Category.Should().Be("structural");
        rule.RelationshipType.Should().Be("Inheritance");
        rule.Enabled.Should().BeTrue();
        rule.Priority.Should().Be(100);
        rule.Detection.Should().HaveCount(1);
        rule.Detection[0].Strategy.Should().Be("ast");
        rule.Detection[0].Language.Should().Be("csharp");
        rule.Detection[0].Patterns.Should().HaveCount(1);
        rule.Confidence.BaseScore.Should().Be(0.95f);
        rule.Confidence.MinimumThreshold.Should().Be(0.90f);
    }

    [TestMethod]
    public void LoadFromYaml_MultipleRules_ParsesAll()
    {
        // Arrange
        var yaml = @"
rules:
  - id: rule-1
    name: Rule 1
    category: structural
    relationshipType: Type1
    detection:
      - strategy: ast
        patterns:
          - nodeType: ClassDeclaration
    output:
      properties:
        - name: test
    confidence:
      baseScore: 0.9
  - id: rule-2
    name: Rule 2
    category: lifecycle
    relationshipType: Type2
    detection:
      - strategy: config
        patterns:
          - filePattern: ""*.xml""
    output:
      properties:
        - name: test
    confidence:
      baseScore: 0.8
";

        // Act
        var rules = this.loader.LoadFromYaml(yaml);

        // Assert
        rules.Should().HaveCount(2);
        rules[0].Id.Should().Be("rule-1");
        rules[0].Category.Should().Be("structural");
        rules[1].Id.Should().Be("rule-2");
        rules[1].Category.Should().Be("lifecycle");
    }

    [TestMethod]
    public void LoadFromYaml_DisabledRule_DefaultsToEnabled()
    {
        // Arrange
        var yaml = @"
rules:
  - id: no-enabled-field
    name: Test
    category: test
    relationshipType: Test
    detection:
      - strategy: ast
        patterns:
          - nodeType: ClassDeclaration
    output:
      properties:
        - name: test
    confidence:
      baseScore: 0.9
";

        // Act
        var rules = this.loader.LoadFromYaml(yaml);

        // Assert
        rules.Should().HaveCount(1);
        rules[0].Enabled.Should().BeTrue();
    }

    [TestMethod]
    public void LoadFromYaml_ExplicitlyDisabled_ParsesCorrectly()
    {
        // Arrange
        var yaml = @"
rules:
  - id: disabled-rule
    name: Test
    category: test
    relationshipType: Test
    enabled: false
    detection:
      - strategy: ast
        patterns:
          - nodeType: ClassDeclaration
    output:
      properties:
        - name: test
    confidence:
      baseScore: 0.9
";

        // Act
        var rules = this.loader.LoadFromYaml(yaml);

        // Assert
        rules.Should().HaveCount(1);
        rules[0].Enabled.Should().BeFalse();
    }

    [TestMethod]
    public void LoadFromYaml_EmptyRules_ReturnsEmptyList()
    {
        // Arrange
        var yaml = @"rules: []";

        // Act
        var rules = this.loader.LoadFromYaml(yaml);

        // Assert
        rules.Should().BeEmpty();
    }

    [TestMethod]
    public void LoadFromYaml_MissingRequiredFields_SkipsInvalidRule()
    {
        // Arrange
        var yaml = @"
rules:
  - id: valid-rule
    category: test
    relationshipType: Test
    detection:
      - strategy: ast
        patterns:
          - nodeType: ClassDeclaration
    output:
      properties:
        - name: test
    confidence:
      baseScore: 0.9
  - name: Missing ID Rule
    category: test
    relationshipType: Test
";

        // Act
        var rules = this.loader.LoadFromYaml(yaml);

        // Assert
        rules.Should().HaveCount(1);
        rules[0].Id.Should().Be("valid-rule");
    }

    [TestMethod]
    public void LoadFromYaml_ValidationCriteria_ParsesCorrectly()
    {
        // Arrange
        var yaml = @"
rules:
  - id: with-validation
    name: Test
    category: test
    relationshipType: Test
    detection:
      - strategy: ast
        patterns:
          - nodeType: ClassDeclaration
    validation:
      interface: IService
      baseClass: ServiceBase
      attributes:
        - ServiceAttribute
      methods:
        - name: Initialize
          returnType: void
          required: true
    output:
      properties:
        - name: test
    confidence:
      baseScore: 0.9
";

        // Act
        var rules = this.loader.LoadFromYaml(yaml);

        // Assert
        rules.Should().HaveCount(1);
        var validation = rules[0].Validation;
        validation.Should().NotBeNull();
        validation!.Interface.Should().Be("IService");
        validation.BaseClass.Should().Be("ServiceBase");
        validation.Attributes.Should().Contain("ServiceAttribute");
        validation.Methods.Should().HaveCount(1);
        validation.Methods![0].Name.Should().Be("Initialize");
        validation.Methods[0].ReturnType.Should().Be("void");
        validation.Methods[0].Required.Should().BeTrue();
    }

    [TestMethod]
    public void LoadFromYaml_ExtractExpressions_ParsesCorrectly()
    {
        // Arrange
        var yaml = @"
rules:
  - id: with-extract
    name: Test
    category: test
    relationshipType: Test
    detection:
      - strategy: ast
        patterns:
          - nodeType: ClassDeclaration
            extract:
              className: ""node.Identifier.Text""
              baseClass: ""node.BaseList.Types.First()""
    output:
      properties:
        - name: className
        - name: baseClass
    confidence:
      baseScore: 0.9
";

        // Act
        var rules = this.loader.LoadFromYaml(yaml);

        // Assert
        rules.Should().HaveCount(1);
        var pattern = rules[0].Detection[0].Patterns[0];
        pattern.Extract.Should().NotBeNull();
        pattern.Extract.Should().ContainKey("className");
        pattern.Extract!["className"].Should().Be("node.Identifier.Text");
        pattern.Extract.Should().ContainKey("baseClass");
    }
}
