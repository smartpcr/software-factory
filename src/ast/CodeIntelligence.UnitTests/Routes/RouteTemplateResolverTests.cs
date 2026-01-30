// -----------------------------------------------------------------------
// <copyright file="RouteTemplateResolverTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Routes;

using CodeIntelligence.Routes;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class RouteTemplateResolverTests
{
    private RouteTemplateResolver resolver = null!;

    [TestInitialize]
    public void Setup()
    {
        this.resolver = new RouteTemplateResolver();
    }

    #region Parse Tests

    [TestMethod]
    public void Parse_SimpleRoute_ParsesCorrectly()
    {
        // Arrange
        var template = "api/users";

        // Act
        var result = this.resolver.Parse(template);

        // Assert
        result.NormalizedTemplate.Should().Be("api/users");
        result.Segments.Should().HaveCount(2);
        result.Segments[0].Value.Should().Be("api");
        result.Segments[1].Value.Should().Be("users");
        result.Parameters.Should().BeEmpty();
    }

    [TestMethod]
    public void Parse_RouteWithParameter_ExtractsParameter()
    {
        // Arrange
        var template = "api/users/{id}";

        // Act
        var result = this.resolver.Parse(template);

        // Assert
        result.Parameters.Should().ContainSingle();
        result.Parameters[0].Name.Should().Be("id");
        result.Parameters[0].IsOptional.Should().BeFalse();
    }

    [TestMethod]
    public void Parse_RouteWithConstraint_ExtractsConstraint()
    {
        // Arrange
        var template = "api/users/{id:int}";

        // Act
        var result = this.resolver.Parse(template);

        // Assert
        result.Parameters.Should().ContainSingle();
        result.Parameters[0].Name.Should().Be("id");
        result.Parameters[0].Constraint.Should().Be("int");
        result.Parameters[0].ConstraintType.Should().Be(RouteConstraintType.Int);
    }

    [TestMethod]
    public void Parse_RouteWithOptionalParameter_DetectsOptional()
    {
        // Arrange
        var template = "api/users/{id?}";

        // Act
        var result = this.resolver.Parse(template);

        // Assert
        result.Parameters.Should().ContainSingle();
        result.Parameters[0].IsOptional.Should().BeTrue();
        result.HasOptionalParameters.Should().BeTrue();
    }

    [TestMethod]
    public void Parse_RouteWithCatchAll_DetectsCatchAll()
    {
        // Arrange
        var template = "api/{*path}";

        // Act
        var result = this.resolver.Parse(template);

        // Assert
        result.Parameters.Should().ContainSingle();
        result.Parameters[0].IsCatchAll.Should().BeTrue();
        result.Parameters[0].Name.Should().Be("path");
        result.HasCatchAll.Should().BeTrue();
    }

    [TestMethod]
    public void Parse_RouteWithDoubleCatchAll_DetectsCatchAll()
    {
        // Arrange
        var template = "api/{**slug}";

        // Act
        var result = this.resolver.Parse(template);

        // Assert
        result.Parameters.Should().ContainSingle();
        result.Parameters[0].IsCatchAll.Should().BeTrue();
    }

    [TestMethod]
    public void Parse_RouteWithControllerPlaceholder_DetectsPlaceholder()
    {
        // Arrange
        var template = "api/[controller]";

        // Act
        var result = this.resolver.Parse(template);

        // Assert
        result.Segments.Should().HaveCount(2);
        result.Segments[1].SegmentType.Should().Be(RouteSegmentType.Placeholder);
        result.Segments[1].PlaceholderType.Should().Be(PlaceholderType.Controller);
    }

    [TestMethod]
    public void Parse_RouteWithActionPlaceholder_DetectsPlaceholder()
    {
        // Arrange
        var template = "[controller]/[action]";

        // Act
        var result = this.resolver.Parse(template);

        // Assert
        result.Segments.Should().HaveCount(2);
        result.Segments[0].PlaceholderType.Should().Be(PlaceholderType.Controller);
        result.Segments[1].PlaceholderType.Should().Be(PlaceholderType.Action);
    }

    [TestMethod]
    public void Parse_RouteWithGuidConstraint_ParsesGuidConstraint()
    {
        // Arrange
        var template = "api/orders/{id:guid}";

        // Act
        var result = this.resolver.Parse(template);

        // Assert
        result.Parameters[0].ConstraintType.Should().Be(RouteConstraintType.Guid);
    }

    [TestMethod]
    public void Parse_RouteWithMultipleParameters_ParsesAll()
    {
        // Arrange
        var template = "api/users/{userId}/orders/{orderId}";

        // Act
        var result = this.resolver.Parse(template);

        // Assert
        result.Parameters.Should().HaveCount(2);
        result.Parameters[0].Name.Should().Be("userId");
        result.Parameters[1].Name.Should().Be("orderId");
    }

    #endregion

    #region Combine Tests

    [TestMethod]
    public void Combine_ControllerAndAction_CombinesCorrectly()
    {
        // Arrange
        var controller = "api/users";
        var action = "{id}";

        // Act
        var result = this.resolver.Combine(controller, action);

        // Assert
        result.Should().Be("api/users/{id}");
    }

    [TestMethod]
    public void Combine_ActionWithAbsolutePath_UsesAction()
    {
        // Arrange
        var controller = "api/users";
        var action = "/api/v2/users";

        // Act
        var result = this.resolver.Combine(controller, action);

        // Assert
        result.Should().Be("api/v2/users");
    }

    [TestMethod]
    public void Combine_ActionWithTildePath_UsesAction()
    {
        // Arrange
        var controller = "api/users";
        var action = "~/override/path";

        // Act
        var result = this.resolver.Combine(controller, action);

        // Assert
        result.Should().Be("override/path");
    }

    [TestMethod]
    public void Combine_NullAction_ReturnsController()
    {
        // Arrange
        var controller = "api/users";

        // Act
        var result = this.resolver.Combine(controller, null);

        // Assert
        result.Should().Be("api/users");
    }

    [TestMethod]
    public void Combine_NullController_ReturnsAction()
    {
        // Arrange
        var action = "{id}";

        // Act
        var result = this.resolver.Combine(null, action);

        // Assert
        result.Should().Be("{id}");
    }

    #endregion

    #region ResolvePlaceholders Tests

    [TestMethod]
    public void ResolvePlaceholders_ControllerPlaceholder_Resolves()
    {
        // Arrange
        var template = "api/[controller]";

        // Act
        var result = this.resolver.ResolvePlaceholders(template, "users", null);

        // Assert
        result.Should().Be("api/users");
    }

    [TestMethod]
    public void ResolvePlaceholders_ActionPlaceholder_Resolves()
    {
        // Arrange
        var template = "[controller]/[action]";

        // Act
        var result = this.resolver.ResolvePlaceholders(template, "users", "getall");

        // Assert
        result.Should().Be("users/getall");
    }

    [TestMethod]
    public void ResolvePlaceholders_AreaPlaceholder_Resolves()
    {
        // Arrange
        var template = "[area]/[controller]/[action]";

        // Act
        var result = this.resolver.ResolvePlaceholders(template, "users", "index", "admin");

        // Assert
        result.Should().Be("admin/users/index");
    }

    #endregion

    #region GenerateMatchPattern Tests

    [TestMethod]
    public void GenerateMatchPattern_LiteralRoute_GeneratesPattern()
    {
        // Arrange
        var template = "api/users";

        // Act
        var pattern = this.resolver.GenerateMatchPattern(template);

        // Assert
        pattern.Should().Contain("api");
        pattern.Should().Contain("users");
    }

    [TestMethod]
    public void GenerateMatchPattern_ParameterWithIntConstraint_GeneratesIntPattern()
    {
        // Arrange
        var template = "api/users/{id:int}";

        // Act
        var pattern = this.resolver.GenerateMatchPattern(template);

        // Assert
        pattern.Should().Contain(@"-?\d+");
    }

    [TestMethod]
    public void GenerateMatchPattern_ParameterWithGuidConstraint_GeneratesGuidPattern()
    {
        // Arrange
        var template = "api/orders/{id:guid}";

        // Act
        var pattern = this.resolver.GenerateMatchPattern(template);

        // Assert
        pattern.Should().Contain("[0-9a-fA-F]");
    }

    #endregion

    #region ExtractValues Tests

    [TestMethod]
    public void ExtractValues_SimpleParameter_ExtractsValue()
    {
        // Arrange
        var template = "api/users/{id}";
        var url = "/api/users/123";

        // Act
        var values = this.resolver.ExtractValues(template, url);

        // Assert
        values.Should().ContainKey("id");
        values["id"].Should().Be("123");
    }

    [TestMethod]
    public void ExtractValues_MultipleParameters_ExtractsAll()
    {
        // Arrange
        var template = "api/users/{userId}/orders/{orderId}";
        var url = "/api/users/42/orders/99";

        // Act
        var values = this.resolver.ExtractValues(template, url);

        // Assert
        values.Should().HaveCount(2);
        values["userId"].Should().Be("42");
        values["orderId"].Should().Be("99");
    }

    #endregion
}
