// -----------------------------------------------------------------------
// <copyright file="CSharpHyperedgeExtractorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Csharp.UnitTests;

using Ast.Core;

[TestClass]
public class CSharpHyperedgeExtractorTests
{
    [TestMethod]
    public void Extract_AddSingleton_CreatesServiceRegistrationHyperedge()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IUserService, UserService>();
    }
}";
        var extractor = new CSharpHyperedgeExtractor("Startup.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.ServiceRegistration);
        var regEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.ServiceRegistration);
        regEdge.Metadata["lifetime"].Should().Be("Singleton");
        regEdge.Metadata["service_type"].Should().Be("IUserService");
        regEdge.Metadata["implementation_type"].Should().Be("UserService");
    }

    [TestMethod]
    public void Extract_AddScoped_CapturesScopedLifetime()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<IRepository, SqlRepository>();
    }
}";
        var extractor = new CSharpHyperedgeExtractor("Startup.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var regEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.ServiceRegistration);
        regEdge.Metadata["lifetime"].Should().Be("Scoped");
    }

    [TestMethod]
    public void Extract_AddTransient_CapturesTransientLifetime()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddTransient<IValidator, DataValidator>();
    }
}";
        var extractor = new CSharpHyperedgeExtractor("Startup.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var regEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.ServiceRegistration);
        regEdge.Metadata["lifetime"].Should().Be("Transient");
    }

    [TestMethod]
    public void Extract_AddDbContext_CapturesDbContextRegistration()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));
    }
}";
        var extractor = new CSharpHyperedgeExtractor("Startup.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.ServiceRegistration);
        var regEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.ServiceRegistration);
        regEdge.Metadata["service_type"].Should().Be("AppDbContext");
    }

    [TestMethod]
    public void Extract_ApiController_CreatesApiEndpointHyperedge()
    {
        // Arrange
        var code = @"
[ApiController]
[Route(""api/[controller]"")]
public class UsersController : ControllerBase
{
    [HttpGet(""{id}"")]
    public ActionResult<User> GetUser(int id)
    {
        return Ok();
    }
}";
        var extractor = new CSharpHyperedgeExtractor("UsersController.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.ApiEndpoint);
        var endpoint = hyperedges.First(e => e.EdgeType == HyperedgeType.ApiEndpoint);
        endpoint.Metadata["http_method"].Should().Be("GET");
        endpoint.Metadata["action"].Should().Be("GetUser");
    }

    [TestMethod]
    public void Extract_HttpPost_CapturesPostMethod()
    {
        // Arrange
        var code = @"
[ApiController]
[Route(""api/users"")]
public class UsersController : ControllerBase
{
    [HttpPost]
    public ActionResult<User> CreateUser([FromBody] CreateUserDto dto)
    {
        return Ok();
    }
}";
        var extractor = new CSharpHyperedgeExtractor("UsersController.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var endpoint = hyperedges.First(e => e.EdgeType == HyperedgeType.ApiEndpoint);
        endpoint.Metadata["http_method"].Should().Be("POST");
        endpoint.Roles.Values.Should().Contain(r => r.Contains("body"));
    }

    [TestMethod]
    public void Extract_MainMethod_CreatesEntryPointHyperedge()
    {
        // Arrange
        var code = @"
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        app.Run();
    }
}";
        var extractor = new CSharpHyperedgeExtractor("Program.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.EntryPoint);
        var entry = hyperedges.First(e => e.EdgeType == HyperedgeType.EntryPoint);
        entry.Metadata["uses_web_application"].Should().Be(true);
    }

    [TestMethod]
    public void Extract_TopLevelStatements_CreatesEntryPointHyperedge()
    {
        // Arrange
        var code = @"
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet(""/"", () => ""Hello World"");
app.Run();";
        var extractor = new CSharpHyperedgeExtractor("Program.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.EntryPoint);
        var entry = hyperedges.First(e => e.EdgeType == HyperedgeType.EntryPoint);
        entry.Metadata["is_top_level"].Should().Be(true);
        entry.Metadata["uses_minimal_apis"].Should().Be(true);
    }

    [TestMethod]
    public void Extract_LogInformation_CreatesLoggingCallHyperedge()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private readonly ILogger<OrderService> _logger;

    public void ProcessOrder(int orderId)
    {
        _logger.LogInformation(""Processing order {OrderId}"", orderId);
    }
}";
        var extractor = new CSharpHyperedgeExtractor("OrderService.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.LoggingCall);
        var logEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.LoggingCall);
        logEdge.Metadata["log_level"].Should().Be("Information");
        logEdge.Metadata["structured_fields"]?.ToString().Should().Contain("OrderId");
    }

    [TestMethod]
    public void Extract_LogError_CapturesErrorLevel()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private readonly ILogger _logger;

    public void ProcessOrder()
    {
        try { }
        catch (Exception ex)
        {
            _logger.LogError(ex, ""Failed to process order"");
        }
    }
}";
        var extractor = new CSharpHyperedgeExtractor("OrderService.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var logEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.LoggingCall);
        logEdge.Metadata["log_level"].Should().Be("Error");
        logEdge.Metadata["has_exception"].Should().Be(true);
    }

    [TestMethod]
    public void Extract_HttpClientGetAsync_CreatesHttpClientUsageHyperedge()
    {
        // Arrange
        var code = @"
public class ApiClient
{
    private readonly HttpClient _httpClient;

    public async Task<string> GetData()
    {
        var response = await _httpClient.GetAsync(""https://api.example.com/data"");
        return await response.Content.ReadAsStringAsync();
    }
}";
        var extractor = new CSharpHyperedgeExtractor("ApiClient.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.HttpClientUsage);
        var httpEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.HttpClientUsage);
        httpEdge.Metadata["http_method"].Should().Be("GET");
        httpEdge.Metadata["is_async"].Should().Be(true);
    }

    [TestMethod]
    public void Extract_HttpClientPostAsync_CapturesPostMethod()
    {
        // Arrange
        var code = @"
public class ApiClient
{
    private readonly HttpClient _httpClient;

    public async Task PostData(string data)
    {
        var content = new StringContent(data);
        await _httpClient.PostAsync(""/api/data"", content);
    }
}";
        var extractor = new CSharpHyperedgeExtractor("ApiClient.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var httpEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.HttpClientUsage);
        httpEdge.Metadata["http_method"].Should().Be("POST");
    }

    [TestMethod]
    public void Extract_FileReadAllText_CreatesFileSystemAccessHyperedge()
    {
        // Arrange
        var code = @"
public class ConfigReader
{
    public string ReadConfig()
    {
        return File.ReadAllText(""config.json"");
    }
}";
        var extractor = new CSharpHyperedgeExtractor("ConfigReader.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.FileSystemAccess);
        var fsEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.FileSystemAccess);
        fsEdge.Metadata["operation"].Should().Be("Read");
    }

    [TestMethod]
    public void Extract_FileWriteAllText_CapturesWriteOperation()
    {
        // Arrange
        var code = @"
public class FileWriter
{
    public void WriteData(string path, string content)
    {
        File.WriteAllText(path, content);
    }
}";
        var extractor = new CSharpHyperedgeExtractor("FileWriter.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var fsEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.FileSystemAccess);
        fsEdge.Metadata["operation"].Should().Be("Write");
    }

    [TestMethod]
    public void Extract_DbContextCreation_CreatesDatabaseAccessHyperedge()
    {
        // Arrange
        var code = @"
public class DataService
{
    public void UseDatabase()
    {
        using var context = new AppDbContext(options);
    }
}";
        var extractor = new CSharpHyperedgeExtractor("DataService.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.DatabaseAccess);
        var dbEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.DatabaseAccess);
        dbEdge.Metadata["technology"].Should().Be("EntityFramework");
    }

    [TestMethod]
    public void Extract_SqlConnectionCreation_CapturesSqlServerTechnology()
    {
        // Arrange
        var code = @"
public class DataService
{
    public void UseDatabase()
    {
        using var connection = new SqlConnection(connectionString);
    }
}";
        var extractor = new CSharpHyperedgeExtractor("DataService.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var dbEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.DatabaseAccess);
        dbEdge.Metadata["technology"].Should().Be("SqlServer");
    }

    [TestMethod]
    public void Extract_ConfigurationGetSection_CreatesConfigurationAccessHyperedge()
    {
        // Arrange
        var code = @"
public class ConfigService
{
    private readonly IConfiguration _configuration;

    public void ReadConfig()
    {
        var section = _configuration.GetSection(""Database"");
    }
}";
        var extractor = new CSharpHyperedgeExtractor("ConfigService.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.ConfigurationAccess);
        var configEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.ConfigurationAccess);
        configEdge.Metadata["config_key"].Should().Be("Database");
    }

    [TestMethod]
    public void Extract_UseMiddleware_CreatesMiddlewarePipelineHyperedge()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void Configure(IApplicationBuilder app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }
}";
        var extractor = new CSharpHyperedgeExtractor("Startup.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.MiddlewarePipeline);
        var middlewareEdges = hyperedges.Where(e => e.EdgeType == HyperedgeType.MiddlewarePipeline).ToList();
        middlewareEdges.Should().Contain(e => e.Metadata["middleware_name"]!.ToString() == "Authentication");
        middlewareEdges.Should().Contain(e => e.Metadata["middleware_name"]!.ToString() == "Authorization");
    }

    [TestMethod]
    public void Extract_ControllerWithMultipleEndpoints_CapturesAllEndpoints()
    {
        // Arrange
        var code = @"
[ApiController]
[Route(""api/orders"")]
public class OrdersController : ControllerBase
{
    [HttpGet]
    public ActionResult<List<Order>> GetAll() => Ok();

    [HttpGet(""{id}"")]
    public ActionResult<Order> GetById(int id) => Ok();

    [HttpPost]
    public ActionResult<Order> Create([FromBody] CreateOrderDto dto) => Ok();

    [HttpDelete(""{id}"")]
    public ActionResult Delete(int id) => Ok();
}";
        var extractor = new CSharpHyperedgeExtractor("OrdersController.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var endpoints = hyperedges.Where(e => e.EdgeType == HyperedgeType.ApiEndpoint).ToList();
        endpoints.Should().HaveCount(4);
        endpoints.Should().Contain(e => e.Metadata["http_method"]!.ToString() == "GET" && e.Metadata["action"]!.ToString() == "GetAll");
        endpoints.Should().Contain(e => e.Metadata["http_method"]!.ToString() == "GET" && e.Metadata["action"]!.ToString() == "GetById");
        endpoints.Should().Contain(e => e.Metadata["http_method"]!.ToString() == "POST");
        endpoints.Should().Contain(e => e.Metadata["http_method"]!.ToString() == "DELETE");
    }

    [TestMethod]
    public void Extract_MultipleServiceRegistrations_CapturesAll()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ICache, RedisCache>();
        services.AddScoped<IUserService, UserService>();
        services.AddTransient<IValidator, DataValidator>();
    }
}";
        var extractor = new CSharpHyperedgeExtractor("Startup.cs");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var registrations = hyperedges.Where(e => e.EdgeType == HyperedgeType.ServiceRegistration).ToList();
        registrations.Should().HaveCount(3);
        registrations.Should().Contain(e => e.Metadata["lifetime"]!.ToString() == "Singleton");
        registrations.Should().Contain(e => e.Metadata["lifetime"]!.ToString() == "Scoped");
        registrations.Should().Contain(e => e.Metadata["lifetime"]!.ToString() == "Transient");
    }

    [TestMethod]
    public void ExtractToHypergraph_ReturnsCompleteHypergraph()
    {
        // Arrange
        var code = @"
[ApiController]
[Route(""api/users"")]
public class UsersController : ControllerBase
{
    private readonly ILogger<UsersController> _logger;

    [HttpGet]
    public ActionResult<List<User>> GetAll()
    {
        _logger.LogInformation(""Getting all users"");
        return Ok();
    }
}";
        var extractor = new CSharpHyperedgeExtractor("UsersController.cs");

        // Act
        var hypergraph = extractor.ExtractToHypergraph(code);

        // Assert
        hypergraph.Nodes.Should().NotBeEmpty();
        hypergraph.Hyperedges.Should().NotBeEmpty();
        hypergraph.Metadata.NumCSharpNodes.Should().BeGreaterThan(0);
        hypergraph.Metadata.Files.Should().Contain("UsersController.cs");
    }
}