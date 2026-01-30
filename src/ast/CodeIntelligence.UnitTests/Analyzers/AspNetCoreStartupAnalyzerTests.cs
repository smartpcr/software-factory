// -----------------------------------------------------------------------
// <copyright file="AspNetCoreStartupAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class AspNetCoreStartupAnalyzerTests
{
    private AspNetCoreStartupAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new AspNetCoreStartupAnalyzer();
    }

    [TestMethod]
    public void Analyze_WebApplicationCreateBuilder_DetectsMinimalApi()
    {
        // Arrange
        var code = @"
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.Run();";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "Program.cs");

        // Assert
        results.Should().HaveCount(1);
        results[0].StartupType.Should().Be(AspNetCoreStartupType.MinimalApi);
        results[0].BuilderMethod.Should().Be("CreateBuilder");
        results[0].IsMinimalApi.Should().BeTrue();
    }

    [TestMethod]
    public void Analyze_CreateSlimBuilder_DetectsMinimalApiSlim()
    {
        // Arrange
        var code = @"
var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "Program.cs");

        // Assert
        results.Should().HaveCount(1);
        results[0].StartupType.Should().Be(AspNetCoreStartupType.MinimalApiSlim);
        results[0].BuilderMethod.Should().Be("CreateSlimBuilder");
    }

    [TestMethod]
    public void Analyze_HostCreateDefaultBuilder_DetectsGenericHost()
    {
        // Arrange
        var code = @"
public class Program
{
    public static void Main(string[] args)
    {
        Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) => { })
            .Build()
            .Run();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "Program.cs");

        // Assert
        results.Should().HaveCount(1);
        results[0].StartupType.Should().Be(AspNetCoreStartupType.GenericHost);
        results[0].BuilderMethod.Should().Be("CreateDefaultBuilder");
    }

    [TestMethod]
    public void Analyze_StartupClass_DetectsStartupPattern()
    {
        // Arrange
        var code = @"
namespace MyApp;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers();
    }

    public void Configure(IApplicationBuilder app)
    {
        app.UseRouting();
        app.UseAuthorization();
        app.UseEndpoints(endpoints => { });
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "Startup.cs");

        // Assert
        results.Should().HaveCount(1);
        results[0].StartupType.Should().Be(AspNetCoreStartupType.StartupClass);
        results[0].ClassName.Should().Be("MyApp.Startup");
        results[0].HasConfigureServices.Should().BeTrue();
        results[0].HasConfigure.Should().BeTrue();
        results[0].MiddlewareMethods.Should().Contain("UseRouting");
        results[0].MiddlewareMethods.Should().Contain("UseAuthorization");
    }

    [TestMethod]
    public void Analyze_MinimalApiWithMiddleware_ExtractsMiddlewareChain()
    {
        // Arrange
        var code = @"
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet(""/hello"", () => ""Hello World"");
app.Run();";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "Program.cs");

        // Assert
        results.Should().ContainSingle(r => r.StartupType == AspNetCoreStartupType.MinimalApi);
    }

    [TestMethod]
    public void Analyze_WebHostCreateDefaultBuilder_DetectsWebHost()
    {
        // Arrange
        var code = @"
public class Program
{
    public static void Main(string[] args)
    {
        WebHost.CreateDefaultBuilder(args)
            .UseStartup<Startup>()
            .Build()
            .Run();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "Program.cs");

        // Assert
        results.Should().HaveCount(1);
        results[0].StartupType.Should().Be(AspNetCoreStartupType.WebHost);
        results[0].BuilderMethod.Should().Be("CreateDefaultBuilder");
    }

    [TestMethod]
    public void Analyze_NoStartupPattern_ReturnsEmpty()
    {
        // Arrange
        var code = @"
public class MyService
{
    public void DoWork() { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "MyService.cs");

        // Assert
        results.Should().BeEmpty();
    }

    [TestMethod]
    public void Analyze_StartupClassWithOnlyConfigure_StillDetected()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void Configure(IApplicationBuilder app)
    {
        app.UseRouting();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "Startup.cs");

        // Assert
        results.Should().HaveCount(1);
        results[0].HasConfigureServices.Should().BeFalse();
        results[0].HasConfigure.Should().BeTrue();
    }
}
