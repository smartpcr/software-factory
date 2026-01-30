// -----------------------------------------------------------------------
// <copyright file="FormatterTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Formatters;

using System;
using System.Collections.Generic;
using CodeIntelligence.Analyzers;
using CodeIntelligence.Formatters;
using CodeIntelligence.Services;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Unit tests for output formatters.
/// </summary>
[TestClass]
public class FormatterTests
{
    private RepositoryAnalysisResult sampleResult = null!;
    private AnalysisSummary sampleSummary = null!;

    [TestInitialize]
    public void Setup()
    {
        this.sampleResult = new RepositoryAnalysisResult
        {
            RepositoryPath = "/test/repo",
            AnalyzedAt = DateTime.UtcNow,
            TotalFilesAnalyzed = 10,
            ApiEndpoints = new List<HttpEndpointInfo>
            {
                new HttpEndpointInfo
                {
                    EndpointType = HttpEndpointType.AttributeBased,
                    HttpMethod = "GET",
                    RouteTemplate = "/api/orders",
                    ControllerName = "OrdersController",
                    ActionName = "GetAll",
                    ReturnType = "IActionResult",
                    FilePath = "OrdersController.cs",
                    LineNumber = 10,
                },
                new HttpEndpointInfo
                {
                    EndpointType = HttpEndpointType.AttributeBased,
                    HttpMethod = "POST",
                    RouteTemplate = "/api/orders",
                    ControllerName = "OrdersController",
                    ActionName = "Create",
                    ReturnType = "IActionResult",
                    FilePath = "OrdersController.cs",
                    LineNumber = 20,
                },
            },
            AuthorizationInfo = new List<AuthorizationInfo>
            {
                new AuthorizationInfo
                {
                    AuthorizationType = AuthorizationType.Authorize,
                    Scope = AuthorizationScope.Class,
                    ClassName = "OrdersController",
                    FilePath = "OrdersController.cs",
                    LineNumber = 5,
                    Confidence = 0.95f,
                },
            },
            LogStatements = new List<LogStatementInfo>
            {
                new LogStatementInfo
                {
                    LoggerType = LoggerType.ILogger,
                    Level = LogLevel.Information,
                    MessageTemplate = "Processing order {OrderId}",
                    FilePath = "OrderService.cs",
                    LineNumber = 15,
                },
            },
            DatabaseEntities = new List<DatabaseEntityInfo>
            {
                new DatabaseEntityInfo
                {
                    ClassName = "Order",
                    TableName = "Orders",
                    EntityType = DatabaseEntityType.Table,
                    Properties = new List<EntityPropertyInfo>(),
                    FilePath = "Order.cs",
                    LineNumber = 1,
                    Confidence = 0.95f,
                },
            },
        };

        this.sampleSummary = new AnalysisSummary
        {
            RepositoryPath = "/test/repo",
            TotalFilesAnalyzed = 10,
            ErrorCount = 0,
            ApiEndpointCount = 2,
            HttpClientCount = 1,
            AuthorizationCount = 1,
            LogStatementCount = 5,
            MetricCount = 2,
            DatabaseEntityCount = 3,
            AggregateCount = 1,
            ResiliencePolicyCount = 2,
        };
    }

    #region JsonFormatter Tests

    [TestMethod]
    public void JsonFormatter_FormatName_ReturnsJson()
    {
        // Arrange
        var formatter = new JsonFormatter();

        // Assert
        formatter.FormatName.Should().Be("json");
    }

    [TestMethod]
    public void JsonFormatter_Format_ReturnsValidJson()
    {
        // Arrange
        var formatter = new JsonFormatter();

        // Act
        var output = formatter.Format(this.sampleResult);

        // Assert
        output.Should().NotBeNullOrEmpty();
        output.Should().Contain("repositoryPath");
        output.Should().Contain("/test/repo");
        output.Should().Contain("apiEndpoints");
    }

    [TestMethod]
    public void JsonFormatter_FormatSummary_ReturnsValidJson()
    {
        // Arrange
        var formatter = new JsonFormatter();

        // Act
        var output = formatter.FormatSummary(this.sampleSummary);

        // Assert
        output.Should().NotBeNullOrEmpty();
        output.Should().Contain("totalFilesAnalyzed");
        output.Should().Contain("10");
    }

    [TestMethod]
    public void JsonFormatter_Format_UsesIndentation()
    {
        // Arrange
        var formatter = new JsonFormatter();

        // Act
        var output = formatter.Format(this.sampleResult);

        // Assert
        output.Should().Contain("\n");
        output.Should().Contain("  ");
    }

    #endregion

    #region TableFormatter Tests

    [TestMethod]
    public void TableFormatter_FormatName_ReturnsTable()
    {
        // Arrange
        var formatter = new TableFormatter();

        // Assert
        formatter.FormatName.Should().Be("table");
    }

    [TestMethod]
    public void TableFormatter_Format_ContainsHeader()
    {
        // Arrange
        var formatter = new TableFormatter();

        // Act
        var output = formatter.Format(this.sampleResult);

        // Assert
        output.Should().Contain("Repository Analysis");
        output.Should().Contain("/test/repo");
    }

    [TestMethod]
    public void TableFormatter_Format_ContainsApiEndpoints()
    {
        // Arrange
        var formatter = new TableFormatter();

        // Act
        var output = formatter.Format(this.sampleResult);

        // Assert
        output.Should().Contain("API Endpoints");
        output.Should().Contain("GET");
        output.Should().Contain("/api/orders");
    }

    [TestMethod]
    public void TableFormatter_Format_ContainsSecurity()
    {
        // Arrange
        var formatter = new TableFormatter();

        // Act
        var output = formatter.Format(this.sampleResult);

        // Assert
        output.Should().Contain("Security");
        output.Should().Contain("Authorization Requirements");
    }

    [TestMethod]
    public void TableFormatter_FormatSummary_ContainsCounts()
    {
        // Arrange
        var formatter = new TableFormatter();

        // Act
        var output = formatter.FormatSummary(this.sampleSummary);

        // Assert
        output.Should().Contain("Analysis Summary");
        output.Should().Contain("Files Analyzed");
        output.Should().Contain("10");
    }

    #endregion

    #region MermaidFormatter Tests

    [TestMethod]
    public void MermaidFormatter_FormatName_ReturnsMermaid()
    {
        // Arrange
        var formatter = new MermaidFormatter();

        // Assert
        formatter.FormatName.Should().Be("mermaid");
    }

    [TestMethod]
    public void MermaidFormatter_Format_ContainsMermaidSyntax()
    {
        // Arrange
        var formatter = new MermaidFormatter();

        // Act
        var output = formatter.Format(this.sampleResult);

        // Assert
        output.Should().Contain("```mermaid");
        output.Should().Contain("graph TB");
        output.Should().Contain("```");
    }

    [TestMethod]
    public void MermaidFormatter_Format_ContainsApiLayer()
    {
        // Arrange
        var formatter = new MermaidFormatter();

        // Act
        var output = formatter.Format(this.sampleResult);

        // Assert
        output.Should().Contain("API");
        output.Should().Contain("GET");
    }

    [TestMethod]
    public void MermaidFormatter_Format_ContainsDataLayer()
    {
        // Arrange
        var formatter = new MermaidFormatter();

        // Act
        var output = formatter.Format(this.sampleResult);

        // Assert
        output.Should().Contain("Data");
        output.Should().Contain("Order");
    }

    [TestMethod]
    public void MermaidFormatter_FormatSummary_ContainsPieChart()
    {
        // Arrange
        var formatter = new MermaidFormatter();

        // Act
        var output = formatter.FormatSummary(this.sampleSummary);

        // Assert
        output.Should().Contain("```mermaid");
        output.Should().Contain("pie");
        output.Should().Contain("API Endpoints");
    }

    [TestMethod]
    public void MermaidFormatter_FormatApiFlow_ContainsFlowchart()
    {
        // Arrange
        var formatter = new MermaidFormatter();

        // Act
        var output = formatter.FormatApiFlow(this.sampleResult);

        // Assert
        output.Should().Contain("```mermaid");
        output.Should().Contain("flowchart");
        output.Should().Contain("OrdersController");
    }

    #endregion
}
