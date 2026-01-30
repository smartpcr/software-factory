// -----------------------------------------------------------------------
// <copyright file="ApplicationLifetimeAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class ApplicationLifetimeAnalyzerTests
{
    private ApplicationLifetimeAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new ApplicationLifetimeAnalyzer();
    }

    [TestMethod]
    public void Analyze_ApplicationStartedRegistration_DetectsEvent()
    {
        // Arrange
        var code = @"
public class StartupService
{
    private readonly IHostApplicationLifetime _lifetime;

    public StartupService(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
        _lifetime.ApplicationStarted.Register(OnStarted);
    }

    private void OnStarted()
    {
        Console.WriteLine(""App started"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "StartupService.cs");

        // Assert
        results.Should().Contain(r => r.EventType == ApplicationLifetimeEventType.Started);
        var startedEvent = results.First(r => r.EventType == ApplicationLifetimeEventType.Started);
        startedEvent.EventName.Should().Be("ApplicationStarted");
        startedEvent.IsRegistration.Should().BeTrue();
        startedEvent.CallbackMethod.Should().Be("OnStarted");
    }

    [TestMethod]
    public void Analyze_ApplicationStoppingRegistration_DetectsEvent()
    {
        // Arrange
        var code = @"
public class ShutdownHandler
{
    public ShutdownHandler(IHostApplicationLifetime lifetime)
    {
        lifetime.ApplicationStopping.Register(() => CleanupResources());
    }

    private void CleanupResources() { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "ShutdownHandler.cs");

        // Assert
        results.Should().Contain(r => r.EventType == ApplicationLifetimeEventType.Stopping);
    }

    [TestMethod]
    public void Analyze_ApplicationStoppedRegistration_DetectsEvent()
    {
        // Arrange
        var code = @"
public class CleanupService
{
    public CleanupService(IHostApplicationLifetime appLifetime)
    {
        appLifetime.ApplicationStopped.Register(FinalCleanup);
    }

    private void FinalCleanup() { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "CleanupService.cs");

        // Assert
        results.Should().Contain(r => r.EventType == ApplicationLifetimeEventType.Stopped);
    }

    [TestMethod]
    public void Analyze_StopApplicationCall_DetectsMethodCall()
    {
        // Arrange
        var code = @"
public class GracefulShutdown
{
    private readonly IHostApplicationLifetime _lifetime;

    public void TriggerShutdown()
    {
        _lifetime.StopApplication();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "GracefulShutdown.cs");

        // Assert
        results.Should().Contain(r => r.EventType == ApplicationLifetimeEventType.MethodCall);
        var methodCall = results.First(r => r.EventType == ApplicationLifetimeEventType.MethodCall);
        methodCall.MethodName.Should().Be("StopApplication");
    }

    [TestMethod]
    public void Analyze_LifetimeInjection_DetectsConstructorParameter()
    {
        // Arrange
        var code = @"
public class MyService
{
    public MyService(IHostApplicationLifetime lifetime)
    {
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "MyService.cs");

        // Assert
        results.Should().Contain(r => r.EventType == ApplicationLifetimeEventType.Injection);
        var injection = results.First(r => r.EventType == ApplicationLifetimeEventType.Injection);
        injection.ParameterName.Should().Be("lifetime");
        injection.ParameterType.Should().Contain("IHostApplicationLifetime");
    }

    [TestMethod]
    public void Analyze_IApplicationLifetime_DetectsOlderInterface()
    {
        // Arrange
        var code = @"
public class LegacyService
{
    public LegacyService(IApplicationLifetime lifetime)
    {
        lifetime.ApplicationStarted.Register(OnStart);
    }

    private void OnStart() { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "LegacyService.cs");

        // Assert
        results.Should().Contain(r => r.EventType == ApplicationLifetimeEventType.Started);
        results.Should().Contain(r => r.EventType == ApplicationLifetimeEventType.Injection);
    }

    [TestMethod]
    public void Analyze_NoLifetimeUsage_ReturnsEmpty()
    {
        // Arrange
        var code = @"
public class SimpleService
{
    public void DoWork() { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "SimpleService.cs");

        // Assert
        results.Should().BeEmpty();
    }

    [TestMethod]
    public void Analyze_MultipleLifetimeEvents_DetectsAll()
    {
        // Arrange
        var code = @"
public class LifecycleHandler
{
    public LifecycleHandler(IHostApplicationLifetime lifetime)
    {
        lifetime.ApplicationStarted.Register(OnStarted);
        lifetime.ApplicationStopping.Register(OnStopping);
        lifetime.ApplicationStopped.Register(OnStopped);
    }

    private void OnStarted() { }
    private void OnStopping() { }
    private void OnStopped() { }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.Analyze(tree, "LifecycleHandler.cs");

        // Assert
        results.Should().Contain(r => r.EventType == ApplicationLifetimeEventType.Started);
        results.Should().Contain(r => r.EventType == ApplicationLifetimeEventType.Stopping);
        results.Should().Contain(r => r.EventType == ApplicationLifetimeEventType.Stopped);
        results.Should().Contain(r => r.EventType == ApplicationLifetimeEventType.Injection);
    }
}
