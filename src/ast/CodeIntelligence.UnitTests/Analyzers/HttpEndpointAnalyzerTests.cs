// -----------------------------------------------------------------------
// <copyright file="HttpEndpointAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class HttpEndpointAnalyzerTests
{
    private HttpEndpointAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new HttpEndpointAnalyzer();
    }

    #region Attribute-Based Endpoints

    [TestMethod]
    public void Analyze_HttpGetAttribute_DetectsGetEndpoint()
    {
        // Arrange
        var code = @"
[ApiController]
[Route(""api/[controller]"")]
public class UsersController : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll() => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "UsersController.cs");

        // Assert
        endpoints.Should().ContainSingle();
        var endpoint = endpoints[0];
        endpoint.HttpMethod.Should().Be("GET");
        endpoint.EndpointType.Should().Be(HttpEndpointType.AttributeBased);
        endpoint.ControllerName.Should().Be("Users");
        endpoint.ActionName.Should().Be("GetAll");
    }

    [TestMethod]
    public void Analyze_HttpGetWithRoute_ExtractsRouteTemplate()
    {
        // Arrange
        var code = @"
[ApiController]
[Route(""api/users"")]
public class UsersController : ControllerBase
{
    [HttpGet(""{id}"")]
    public IActionResult GetById(int id) => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "UsersController.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].RouteTemplate.Should().Be("api/users/{id}");
    }

    [TestMethod]
    public void Analyze_HttpPostAttribute_DetectsPostEndpoint()
    {
        // Arrange
        var code = @"
[ApiController]
[Route(""api/users"")]
public class UsersController : ControllerBase
{
    [HttpPost]
    public IActionResult Create([FromBody] UserDto user) => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "UsersController.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].HttpMethod.Should().Be("POST");
    }

    [TestMethod]
    public void Analyze_HttpPutAttribute_DetectsPutEndpoint()
    {
        // Arrange
        var code = @"
[Route(""api/users"")]
public class UsersController : ControllerBase
{
    [HttpPut(""{id}"")]
    public IActionResult Update(int id, [FromBody] UserDto user) => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "UsersController.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].HttpMethod.Should().Be("PUT");
        endpoints[0].RouteTemplate.Should().Contain("{id}");
    }

    [TestMethod]
    public void Analyze_HttpDeleteAttribute_DetectsDeleteEndpoint()
    {
        // Arrange
        var code = @"
[Route(""api/users"")]
public class UsersController : ControllerBase
{
    [HttpDelete(""{id}"")]
    public IActionResult Delete(int id) => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "UsersController.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].HttpMethod.Should().Be("DELETE");
    }

    [TestMethod]
    public void Analyze_HttpPatchAttribute_DetectsPatchEndpoint()
    {
        // Arrange
        var code = @"
[Route(""api/users"")]
public class UsersController : ControllerBase
{
    [HttpPatch(""{id}"")]
    public IActionResult PartialUpdate(int id) => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "UsersController.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].HttpMethod.Should().Be("PATCH");
    }

    [TestMethod]
    public void Analyze_AuthorizeAttribute_DetectsAuthRequirement()
    {
        // Arrange
        var code = @"
[Authorize]
[Route(""api/admin"")]
public class AdminController : ControllerBase
{
    [HttpGet]
    public IActionResult GetSecure() => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "AdminController.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].RequiresAuthorization.Should().BeTrue();
    }

    [TestMethod]
    public void Analyze_AllowAnonymousAttribute_DetectsAnonymousAccess()
    {
        // Arrange
        var code = @"
[Authorize]
[Route(""api/users"")]
public class UsersController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet(""public"")]
    public IActionResult GetPublic() => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "UsersController.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].AllowsAnonymous.Should().BeTrue();
    }

    [TestMethod]
    public void Analyze_FromBodyParameter_IdentifiesBodySource()
    {
        // Arrange
        var code = @"
[Route(""api/users"")]
public class UsersController : ControllerBase
{
    [HttpPost]
    public IActionResult Create([FromBody] UserDto user) => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "UsersController.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].Parameters.Should().ContainSingle();
        endpoints[0].Parameters[0].Source.Should().Be(ParameterSource.Body);
    }

    [TestMethod]
    public void Analyze_FromQueryParameter_IdentifiesQuerySource()
    {
        // Arrange
        var code = @"
[Route(""api/users"")]
public class UsersController : ControllerBase
{
    [HttpGet]
    public IActionResult Search([FromQuery] string name) => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "UsersController.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].Parameters.Should().ContainSingle();
        endpoints[0].Parameters[0].Source.Should().Be(ParameterSource.Query);
    }

    [TestMethod]
    public void Analyze_RouteConstraint_ParsesConstraint()
    {
        // Arrange
        var code = @"
[Route(""api/users"")]
public class UsersController : ControllerBase
{
    [HttpGet(""{id:int}"")]
    public IActionResult GetById(int id) => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "UsersController.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].RouteTemplate.Should().Contain("{id:int}");
    }

    [TestMethod]
    public void Analyze_OptionalRouteParameter_DetectsOptional()
    {
        // Arrange
        var code = @"
[Route(""api/users"")]
public class UsersController : ControllerBase
{
    [HttpGet(""{id?}"")]
    public IActionResult GetById(int? id = null) => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "UsersController.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].RouteTemplate.Should().Contain("{id?}");
        endpoints[0].Parameters.Should().Contain(p => p.IsOptional);
    }

    #endregion

    #region Minimal API Endpoints

    [TestMethod]
    public void Analyze_MapGet_DetectsMinimalApiEndpoint()
    {
        // Arrange
        var code = @"
var app = builder.Build();
app.MapGet(""/api/users"", () => Results.Ok());";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "Program.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].HttpMethod.Should().Be("GET");
        endpoints[0].EndpointType.Should().Be(HttpEndpointType.MinimalApi);
        endpoints[0].RouteTemplate.Should().Be("/api/users");
    }

    [TestMethod]
    public void Analyze_MapPost_DetectsMinimalApiEndpoint()
    {
        // Arrange
        var code = @"
app.MapPost(""/api/users"", (UserDto user) => Results.Created());";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "Program.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].HttpMethod.Should().Be("POST");
    }

    [TestMethod]
    public void Analyze_MapPut_DetectsMinimalApiEndpoint()
    {
        // Arrange
        var code = @"
app.MapPut(""/api/users/{id}"", (int id, UserDto user) => Results.Ok());";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "Program.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].HttpMethod.Should().Be("PUT");
    }

    [TestMethod]
    public void Analyze_MapDelete_DetectsMinimalApiEndpoint()
    {
        // Arrange
        var code = @"
app.MapDelete(""/api/users/{id}"", (int id) => Results.NoContent());";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "Program.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].HttpMethod.Should().Be("DELETE");
    }

    [TestMethod]
    public void Analyze_MapPatch_DetectsMinimalApiEndpoint()
    {
        // Arrange
        var code = @"
app.MapPatch(""/api/users/{id}"", (int id) => Results.Ok());";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "Program.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].HttpMethod.Should().Be("PATCH");
    }

    [TestMethod]
    public void Analyze_MinimalApiWithAsyncHandler_DetectsAsync()
    {
        // Arrange
        var code = @"
app.MapGet(""/api/users"", async () => await Task.FromResult(Results.Ok()));";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "Program.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].IsAsync.Should().BeTrue();
    }

    #endregion

    #region Convention-Based Endpoints

    [TestMethod]
    public void Analyze_ControllerNamingConvention_DetectsController()
    {
        // Arrange
        var code = @"
public class ProductsController : Controller
{
    public IActionResult Index() => View();
    public IActionResult Details(int id) => View();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "ProductsController.cs");

        // Assert
        endpoints.Should().HaveCount(2);
        endpoints.Should().AllSatisfy(e => e.EndpointType.Should().Be(HttpEndpointType.ConventionBased));
    }

    [TestMethod]
    public void Analyze_NonActionAttribute_ExcludesMethod()
    {
        // Arrange
        var code = @"
public class ProductsController : Controller
{
    public IActionResult Index() => View();

    [NonAction]
    public string HelperMethod() => ""helper"";
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "ProductsController.cs");

        // Assert
        endpoints.Should().ContainSingle();
        endpoints[0].ActionName.Should().Be("Index");
    }

    [TestMethod]
    public void Analyze_MethodNameConvention_InfersHttpMethod()
    {
        // Arrange
        var code = @"
public class ProductsController : ControllerBase
{
    public IActionResult GetProduct() => Ok();
    public IActionResult CreateProduct() => Ok();
    public IActionResult UpdateProduct() => Ok();
    public IActionResult DeleteProduct() => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "ProductsController.cs");

        // Assert
        endpoints.Should().HaveCount(4);
        endpoints.Should().Contain(e => e.ActionName == "GetProduct" && e.HttpMethod == "GET");
        endpoints.Should().Contain(e => e.ActionName == "CreateProduct" && e.HttpMethod == "POST");
        endpoints.Should().Contain(e => e.ActionName == "UpdateProduct" && e.HttpMethod == "PUT");
        endpoints.Should().Contain(e => e.ActionName == "DeleteProduct" && e.HttpMethod == "DELETE");
    }

    #endregion

    [TestMethod]
    public void Analyze_NoEndpoints_ReturnsEmpty()
    {
        // Arrange
        var code = @"
public class MyService
{
    public void DoWork() { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "MyService.cs");

        // Assert
        endpoints.Should().BeEmpty();
    }

    [TestMethod]
    public void Analyze_MultipleEndpointsInController_DetectsAll()
    {
        // Arrange
        var code = @"
[Route(""api/orders"")]
public class OrdersController : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll() => Ok();

    [HttpGet(""{id}"")]
    public IActionResult GetById(int id) => Ok();

    [HttpPost]
    public IActionResult Create([FromBody] OrderDto order) => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var endpoints = this.analyzer.Analyze(tree, "OrdersController.cs");

        // Assert
        endpoints.Should().HaveCount(3);
        endpoints.Should().Contain(e => e.HttpMethod == "GET" && !e.RouteTemplate.Contains("{id}"));
        endpoints.Should().Contain(e => e.HttpMethod == "GET" && e.RouteTemplate.Contains("{id}"));
        endpoints.Should().Contain(e => e.HttpMethod == "POST");
    }
}
