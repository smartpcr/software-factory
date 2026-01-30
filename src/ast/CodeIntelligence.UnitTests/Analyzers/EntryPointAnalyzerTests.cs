// -----------------------------------------------------------------------
// <copyright file="EntryPointAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="EntryPointAnalyzer"/>.
/// </summary>
[TestClass]
public class EntryPointAnalyzerTests
{
    private EntryPointAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new EntryPointAnalyzer();
    }

    [TestMethod]
    public void Analyze_VoidMain_DetectsEntryPoint()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class Program
    {
        public static void Main()
        {
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entryPoints = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        entryPoints.Should().HaveCount(1);
        var ep = entryPoints[0];
        ep.EntryPointType.Should().Be(EntryPointType.ConsoleMain);
        ep.MethodName.Should().Be("Main");
        ep.ReturnType.Should().Be("void");
        ep.IsAsync.Should().BeFalse();
    }

    [TestMethod]
    public void Analyze_IntMain_DetectsEntryPoint()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class Program
    {
        public static int Main()
        {
            return 0;
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entryPoints = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        entryPoints.Should().HaveCount(1);
        entryPoints[0].ReturnType.Should().Be("int");
    }

    [TestMethod]
    public void Analyze_AsyncTaskMain_DetectsAsyncEntryPoint()
    {
        // Arrange
        var code = @"
using System.Threading.Tasks;

namespace Test
{
    public class Program
    {
        public static async Task Main()
        {
            await Task.CompletedTask;
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entryPoints = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        entryPoints.Should().HaveCount(1);
        var ep = entryPoints[0];
        ep.ReturnType.Should().Be("Task");
        ep.IsAsync.Should().BeTrue();
    }

    [TestMethod]
    public void Analyze_MainWithArgs_DetectsParameters()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class Program
    {
        public static void Main(string[] args)
        {
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entryPoints = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        entryPoints.Should().HaveCount(1);
        var ep = entryPoints[0];
        ep.Parameters.Should().HaveCount(1);
        ep.Parameters[0].Name.Should().Be("args");
        ep.Parameters[0].Type.Should().Be("string[]");
    }

    [TestMethod]
    public void Analyze_TopLevelStatements_DetectsEntryPoint()
    {
        // Arrange
        var code = @"
Console.WriteLine(""Hello, World!"");
";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entryPoints = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        entryPoints.Should().Contain(ep => ep.EntryPointType == EntryPointType.TopLevelStatements);
    }

    [TestMethod]
    public void Analyze_BackgroundService_DetectsEntryPoint()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class Worker : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entryPoints = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        entryPoints.Should().HaveCount(1);
        var ep = entryPoints[0];
        ep.EntryPointType.Should().Be(EntryPointType.BackgroundService);
        ep.BaseType.Should().Be("BackgroundService");
    }

    [TestMethod]
    public void Analyze_IHostedService_DetectsEntryPoint()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class MyService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entryPoints = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        entryPoints.Should().HaveCount(1);
        var ep = entryPoints[0];
        ep.EntryPointType.Should().Be(EntryPointType.HostedService);
    }

    [TestMethod]
    public void Analyze_ServiceBase_DetectsWindowsService()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class MyWindowsService : ServiceBase
    {
        protected override void OnStart(string[] args) { }
        protected override void OnStop() { }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entryPoints = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        entryPoints.Should().HaveCount(1);
        var ep = entryPoints[0];
        ep.EntryPointType.Should().Be(EntryPointType.WindowsService);
    }

    [TestMethod]
    public void Analyze_StatelessService_DetectsServiceFabric()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class MyStatelessService : StatelessService
    {
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entryPoints = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        entryPoints.Should().HaveCount(1);
        entryPoints[0].EntryPointType.Should().Be(EntryPointType.ServiceFabric);
    }

    [TestMethod]
    public void Analyze_IMiddleware_DetectsMiddleware()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class MyMiddleware : IMiddleware
    {
        public Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            return next(context);
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entryPoints = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        entryPoints.Should().HaveCount(1);
        var ep = entryPoints[0];
        ep.EntryPointType.Should().Be(EntryPointType.Middleware);
        ep.MiddlewareType.Should().Be("IMiddleware");
    }

    [TestMethod]
    public void Analyze_ConventionMiddleware_DetectsMiddleware()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class CustomMiddleware
    {
        private readonly RequestDelegate _next;
        
        public CustomMiddleware(RequestDelegate next)
        {
            _next = next;
        }
        
        public async Task InvokeAsync(HttpContext context)
        {
            await _next(context);
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entryPoints = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        entryPoints.Should().HaveCount(1);
        var ep = entryPoints[0];
        ep.EntryPointType.Should().Be(EntryPointType.Middleware);
        ep.MiddlewareType.Should().Be("Convention");
    }

    [TestMethod]
    public void Analyze_IStartupFilter_DetectsStartupHook()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class MyStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return next;
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entryPoints = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        entryPoints.Should().HaveCount(1);
        entryPoints[0].EntryPointType.Should().Be(EntryPointType.StartupHook);
    }

    [TestMethod]
    public void Analyze_QuartzJob_DetectsScheduledJob()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class MyJob : IJob
    {
        public Task Execute(IJobExecutionContext context)
        {
            return Task.CompletedTask;
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entryPoints = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        entryPoints.Should().HaveCount(1);
        var ep = entryPoints[0];
        ep.EntryPointType.Should().Be(EntryPointType.ScheduledJob);
        ep.ScheduleType.Should().Be("Quartz");
    }

    [TestMethod]
    public void Analyze_LifecycleMethods_Detected()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class Worker : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken) { }
        public override Task StartAsync(CancellationToken cancellationToken) => base.StartAsync(cancellationToken);
        public override Task StopAsync(CancellationToken cancellationToken) => base.StopAsync(cancellationToken);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entryPoints = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        entryPoints.Should().HaveCount(1);
        var ep = entryPoints[0];
        ep.LifecycleMethods.Should().Contain("ExecuteAsync");
        ep.LifecycleMethods.Should().Contain("StartAsync");
        ep.LifecycleMethods.Should().Contain("StopAsync");
    }
}
