// -----------------------------------------------------------------------
// <copyright file="MiddlewareChainBuilderTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="MiddlewareChainBuilder"/>.
/// </summary>
[TestClass]
public class MiddlewareChainBuilderTests
{
    private MiddlewareChainBuilder builder = null!;

    [TestInitialize]
    public void Setup()
    {
        this.builder = new MiddlewareChainBuilder();
    }

    [TestMethod]
    public void Analyze_UseMiddleware_DetectsRegistration()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void Configure(IApplicationBuilder app)
    {
        app.UseMiddleware<CustomMiddleware>();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var registrations = this.builder.Analyze(tree, "test.cs");

        // Assert
        registrations.Should().HaveCount(1);
        var reg = registrations[0];
        reg.MethodName.Should().Be("UseMiddleware");
        reg.MiddlewareName.Should().Be("CustomMiddleware");
    }

    [TestMethod]
    public void Analyze_UseAuthentication_DetectsCategory()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void Configure(IApplicationBuilder app)
    {
        app.UseAuthentication();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var registrations = this.builder.Analyze(tree, "test.cs");

        // Assert
        registrations.Should().HaveCount(1);
        registrations[0].MiddlewareType.Should().Be(MiddlewareCategory.Authentication);
    }

    [TestMethod]
    public void Analyze_UseAuthorization_DetectsCategory()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void Configure(IApplicationBuilder app)
    {
        app.UseAuthorization();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var registrations = this.builder.Analyze(tree, "test.cs");

        // Assert
        registrations.Should().HaveCount(1);
        registrations[0].MiddlewareType.Should().Be(MiddlewareCategory.Authorization);
    }

    [TestMethod]
    public void Analyze_UseRouting_DetectsCategory()
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
        var registrations = this.builder.Analyze(tree, "test.cs");

        // Assert
        registrations.Should().HaveCount(1);
        registrations[0].MiddlewareType.Should().Be(MiddlewareCategory.Routing);
    }

    [TestMethod]
    public void Analyze_Run_DetectsTerminalMiddleware()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void Configure(IApplicationBuilder app)
    {
        app.Run(async context => await context.Response.WriteAsync(""Hello""));
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var registrations = this.builder.Analyze(tree, "test.cs");

        // Assert
        registrations.Should().HaveCount(1);
        registrations[0].IsTerminal.Should().BeTrue();
        registrations[0].MiddlewareType.Should().Be(MiddlewareCategory.Terminal);
    }

    [TestMethod]
    public void Analyze_MapWhen_DetectsBranchingMiddleware()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void Configure(IApplicationBuilder app)
    {
        app.MapWhen(context => context.Request.Path.StartsWithSegments(""/api""), branch => { });
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var registrations = this.builder.Analyze(tree, "test.cs");

        // Assert
        registrations.Should().HaveCount(1);
        registrations[0].IsBranching.Should().BeTrue();
    }

    [TestMethod]
    public void Analyze_MultipleMiddleware_PreservesOrder()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void Configure(IApplicationBuilder app)
    {
        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints => { });
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var registrations = this.builder.Analyze(tree, "test.cs");

        // Assert
        registrations.Should().HaveCount(6);
        registrations[0].MethodName.Should().Be("UseHttpsRedirection");
        registrations[0].Order.Should().Be(0);
        registrations[5].MethodName.Should().Be("UseEndpoints");
        registrations[5].Order.Should().Be(5);
    }

    [TestMethod]
    public void BuildChain_LinksNodes()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void Configure(IApplicationBuilder app)
    {
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);
        var registrations = this.builder.Analyze(tree, "test.cs");

        // Act
        var chain = this.builder.BuildChain(registrations);

        // Assert
        chain.Nodes.Should().HaveCount(3);
        chain.First.Should().NotBeNull();
        chain.First!.Name.Should().Be("UseRouting");
        chain.First.Next.Should().NotBeNull();
        chain.First.Next!.Name.Should().Be("UseAuthentication");
    }

    [TestMethod]
    public void Analyze_MapGet_DetectsEndpoint()
    {
        // Arrange
        var code = @"
var app = WebApplication.Create();
app.MapGet(""/hello"", () => ""Hello World"");
";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var registrations = this.builder.Analyze(tree, "test.cs");

        // Assert
        registrations.Should().Contain(r => r.MethodName == "MapGet");
        var mapGet = registrations.First(r => r.MethodName == "MapGet");
        mapGet.MiddlewareType.Should().Be(MiddlewareCategory.Endpoints);
        mapGet.MiddlewareName.Should().Contain("/hello");
    }

    [TestMethod]
    public void Analyze_MapHub_DetectsSignalRHub()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void Configure(IApplicationBuilder app)
    {
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapHub<ChatHub>(""/chat"");
        });
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var registrations = this.builder.Analyze(tree, "test.cs");

        // Assert
        var mapHub = registrations.FirstOrDefault(r => r.MethodName == "MapHub");
        mapHub.Should().NotBeNull();
        mapHub!.MiddlewareName.Should().Be("ChatHub");
    }
}
