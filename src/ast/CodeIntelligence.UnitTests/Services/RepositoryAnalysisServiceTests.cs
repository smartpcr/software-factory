// -----------------------------------------------------------------------
// <copyright file="RepositoryAnalysisServiceTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Services;

using System.IO;
using System.Threading.Tasks;
using CodeIntelligence.Services;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Unit tests for RepositoryAnalysisService.
/// </summary>
[TestClass]
public class RepositoryAnalysisServiceTests
{
    private RepositoryAnalysisService service = null!;
    private string tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        this.service = new RepositoryAnalysisService();
        this.tempDir = Path.Combine(Path.GetTempPath(), "CodeIntelligenceTests", Path.GetRandomFileName());
        Directory.CreateDirectory(this.tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, true);
        }
    }

    [TestMethod]
    public async Task AnalyzeRepositoryAsync_EmptyDirectory_ReturnsEmptyResult()
    {
        // Act
        var result = await this.service.AnalyzeRepositoryAsync(this.tempDir);

        // Assert
        result.Should().NotBeNull();
        result.RepositoryPath.Should().Be(this.tempDir);
        result.TotalFilesAnalyzed.Should().Be(0);
    }

    [TestMethod]
    public async Task AnalyzeRepositoryAsync_WithController_DetectsEndpoints()
    {
        // Arrange
        var code = @"
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route(""api/[controller]"")]
public class OrdersController : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll() => Ok();

    [HttpGet(""{id}"")]
    public IActionResult GetById(int id) => Ok();

    [HttpPost]
    public IActionResult Create([FromBody] object dto) => Ok();
}";
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, "OrdersController.cs"), code);

        // Act
        var result = await this.service.AnalyzeRepositoryAsync(this.tempDir);

        // Assert
        result.TotalFilesAnalyzed.Should().Be(1);
        result.ApiEndpoints.Should().HaveCount(3);
    }

    [TestMethod]
    public async Task AnalyzeRepositoryAsync_WithSecurity_DetectsAuthorization()
    {
        // Arrange
        var code = @"
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[Authorize]
[ApiController]
public class SecureController : ControllerBase
{
    [Authorize(Roles = ""Admin"")]
    [HttpGet]
    public IActionResult AdminOnly() => Ok();

    [AllowAnonymous]
    [HttpGet(""public"")]
    public IActionResult PublicEndpoint() => Ok();
}";
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, "SecureController.cs"), code);

        // Act
        var result = await this.service.AnalyzeRepositoryAsync(this.tempDir);

        // Assert
        result.AuthorizationInfo.Should().HaveCountGreaterThan(0);
    }

    [TestMethod]
    public async Task AnalyzeRepositoryAsync_WithLogging_DetectsLogStatements()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private readonly ILogger<OrderService> _logger;

    public void Process()
    {
        _logger.LogInformation(""Processing order"");
        _logger.LogError(""Order failed"");
    }
}";
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, "OrderService.cs"), code);

        // Act
        var result = await this.service.AnalyzeRepositoryAsync(this.tempDir);

        // Assert
        result.LogStatements.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task AnalyzeRepositoryAsync_WithDatabase_DetectsEntities()
    {
        // Arrange
        var code = @"
using System.ComponentModel.DataAnnotations.Schema;

[Table(""Orders"")]
public class Order
{
    public int Id { get; set; }
    public string Name { get; set; }
}";
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, "Order.cs"), code);

        // Act
        var result = await this.service.AnalyzeRepositoryAsync(this.tempDir);

        // Assert
        result.DatabaseEntities.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task AnalyzeRepositoryAsync_WithExceptions_DetectsThrowCatch()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void Process()
    {
        try
        {
            throw new InvalidOperationException(""Error"");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }
}";
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, "OrderService.cs"), code);

        // Act
        var result = await this.service.AnalyzeRepositoryAsync(this.tempDir);

        // Assert
        result.Exceptions.Should().HaveCountGreaterThan(0);
        result.TryCatchBlocks.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task AnalyzeRepositoryAsync_WithDomain_DetectsAggregates()
    {
        // Arrange
        var code = @"
public class Order : AggregateRoot<OrderId>
{
    public OrderId Id { get; private set; }
}

public record OrderId(Guid Value) : ValueObject;";
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, "Order.cs"), code);

        // Act
        var result = await this.service.AnalyzeRepositoryAsync(this.tempDir);

        // Assert
        result.Aggregates.Should().HaveCountGreaterThan(0);
    }

    [TestMethod]
    public async Task AnalyzeRepositoryAsync_SkipsObjBin_ExcludesGeneratedCode()
    {
        // Arrange
        var objDir = Path.Combine(this.tempDir, "obj");
        Directory.CreateDirectory(objDir);
        await File.WriteAllTextAsync(Path.Combine(objDir, "Generated.cs"), "// generated");
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, "Real.cs"), "public class Real { }");

        // Act
        var result = await this.service.AnalyzeRepositoryAsync(this.tempDir);

        // Assert
        result.TotalFilesAnalyzed.Should().Be(1);
    }

    [TestMethod]
    public async Task AnalyzeRepositoryAsync_WithOptions_RespectsFlags()
    {
        // Arrange
        var code = @"
using Microsoft.AspNetCore.Mvc;

[ApiController]
public class TestController : ControllerBase
{
    private readonly ILogger _logger;
    
    [HttpGet]
    public IActionResult Get()
    {
        _logger.LogInformation(""Test"");
        return Ok();
    }
}";
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, "TestController.cs"), code);

        var options = new AnalysisOptions
        {
            AnalyzeApi = true,
            AnalyzeObservability = false,
        };

        // Act
        var result = await this.service.AnalyzeRepositoryAsync(this.tempDir, options);

        // Assert
        result.ApiEndpoints.Should().HaveCount(1);
        result.LogStatements.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetSummary_ReturnsCorrectCounts()
    {
        // Arrange
        var code = @"
using Microsoft.AspNetCore.Mvc;

[ApiController]
public class TestController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
    
    [HttpPost]
    public IActionResult Post() => Ok();
}";
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, "TestController.cs"), code);

        // Act
        var result = await this.service.AnalyzeRepositoryAsync(this.tempDir);
        var summary = this.service.GetSummary(result);

        // Assert
        summary.TotalFilesAnalyzed.Should().Be(1);
        summary.ApiEndpointCount.Should().Be(2);
    }

    [TestMethod]
    public async Task AnalyzeRepositoryAsync_HandlesParseErrors_GracefullyCollectsErrors()
    {
        // Arrange - invalid C# syntax
        var invalidCode = "this is not valid { C# code";
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, "Invalid.cs"), invalidCode);

        // Act
        var result = await this.service.AnalyzeRepositoryAsync(this.tempDir);

        // Assert - should not throw, but may have empty results from unparseable file
        result.Should().NotBeNull();
        result.TotalFilesAnalyzed.Should().Be(1);
    }
}
