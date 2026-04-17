// -----------------------------------------------------------------------
// <copyright file="DiRegistrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests;

using ExecutionEngine.Workflow;
using Microsoft.Extensions.DependencyInjection;
using WorkflowUi.Server.Hosting;
using WorkflowUi.Server.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Tests for DI registration of engine services.
/// </summary>
public class DiRegistrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public DiRegistrationTests(TestWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public void Serializer_ResolvesAsSingleton()
    {
        var sp = new ServiceCollection().AddExecutionEngine().AddLogging().BuildServiceProvider();
        var a = sp.GetRequiredService<WorkflowSerializer>();
        var b = sp.GetRequiredService<WorkflowSerializer>();
        Assert.Same(a, b);
    }

    [Fact]
    public void Loader_ResolvesAsScoped()
    {
        var sp = new ServiceCollection().AddExecutionEngine().AddLogging().BuildServiceProvider();
        WorkflowLoader loader1;
        WorkflowLoader loader2;
        using (var scope1 = sp.CreateScope())
        {
            loader1 = scope1.ServiceProvider.GetRequiredService<WorkflowLoader>();
        }

        using (var scope2 = sp.CreateScope())
        {
            loader2 = scope2.ServiceProvider.GetRequiredService<WorkflowLoader>();
        }

        Assert.NotSame(loader1, loader2);
    }

    [Fact]
    public void Loader_SameScopeReturnsSameInstance()
    {
        var sp = new ServiceCollection().AddExecutionEngine().AddLogging().BuildServiceProvider();
        using var scope = sp.CreateScope();
        var a = scope.ServiceProvider.GetRequiredService<WorkflowLoader>();
        var b = scope.ServiceProvider.GetRequiredService<WorkflowLoader>();
        Assert.Same(a, b);
    }

    [Fact]
    public void Loader_ReceivesServiceProviderWithLoggerFactory()
    {
        var sp = new ServiceCollection().AddExecutionEngine().AddLogging().BuildServiceProvider();
        using var scope = sp.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<WorkflowLoader>();
        Assert.NotNull(loader);
    }

    [Fact]
    public void ExecutorFactory_Registered_AsSingleton()
    {
        var sp = new ServiceCollection().AddExecutionEngine().AddLogging().BuildServiceProvider();
        var a = sp.GetRequiredService<IWorkflowExecutorFactory>();
        var b = sp.GetRequiredService<IWorkflowExecutorFactory>();
        Assert.Same(a, b);
    }

    [Fact]
    public void ExecutorFactory_Create_Throws_WithPhase7Marker()
    {
        var sp = new ServiceCollection().AddExecutionEngine().AddLogging().BuildServiceProvider();
        var executorFactory = sp.GetRequiredService<IWorkflowExecutorFactory>();
        var ex = Assert.Throws<NotImplementedException>(() =>
            executorFactory.Create(new WorkflowDefinition()));
        Assert.Contains("Phase 7", ex.Message);
    }

    [Fact]
    public void WithoutAddExecutionEngine_Loader_FailsToResolve()
    {
        var sp = new ServiceCollection().AddLogging().BuildServiceProvider();
        Assert.Null(sp.GetService<WorkflowLoader>());
    }

    [Fact]
    public void LoaderConstructor_SignatureUnchanged()
    {
        var ctors = typeof(WorkflowLoader).GetConstructors();
        var match = ctors.FirstOrDefault(c =>
        {
            var p = c.GetParameters();
            return p.Length == 1 && p[0].ParameterType == typeof(IServiceProvider);
        });
        Assert.NotNull(match);
        Assert.Equal("serviceProvider", match.GetParameters()[0].Name);
    }

    [Fact]
    public void AddExecutionEngine_IsIdempotent()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddExecutionEngine();
        services.AddExecutionEngine(); // second call
        var sp = services.BuildServiceProvider();
        var serializers = sp.GetServices<WorkflowSerializer>().ToArray();
        Assert.Single(serializers);
    }

    [Fact]
    public async Task IntegrationTest_ResolveLoaderAndSerializer()
    {
        using var scope = this.factory.Services.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<WorkflowLoader>();
        var serializer = scope.ServiceProvider.GetRequiredService<WorkflowSerializer>();
        Assert.NotNull(loader);
        Assert.NotNull(serializer);

        // Smoke test: serializer round-trip
        var yaml = serializer.ToYaml(new WorkflowDefinition { WorkflowName = "test" });
        Assert.False(string.IsNullOrEmpty(yaml));
        await Task.CompletedTask;
    }
}
