// -----------------------------------------------------------------------
// <copyright file="DependencyInjectionAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="DependencyInjectionAnalyzer"/>.
/// </summary>
[TestClass]
public class DependencyInjectionAnalyzerTests
{
    private DependencyInjectionAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new DependencyInjectionAnalyzer();
    }

    [TestMethod]
    public void Analyze_SingleConstructorDependency_DetectsRelationship()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface ILogger { }
    public class MyService
    {
        public MyService(ILogger logger) { }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var rel = relationships[0];
        rel.ConsumerType.Should().Be("Test.MyService");
        rel.DependencyType.Should().Be("ILogger");
        rel.DependencyName.Should().Be("logger");
        rel.InjectionMethod.Should().Be(InjectionMethod.Constructor);
        rel.IsInterface.Should().BeTrue();
    }

    [TestMethod]
    public void Analyze_MultipleConstructorDependencies_DetectsAll()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface ILogger { }
    public interface IRepository { }
    public interface ICache { }
    
    public class MyService
    {
        public MyService(ILogger logger, IRepository repo, ICache cache) { }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(3);
        relationships.Should().Contain(r => r.DependencyType == "ILogger");
        relationships.Should().Contain(r => r.DependencyType == "IRepository");
        relationships.Should().Contain(r => r.DependencyType == "ICache");
    }

    [TestMethod]
    public void Analyze_PropertyInjection_WithInjectAttribute_DetectsRelationship()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface ILogger { }
    
    public class InjectAttribute : System.Attribute { }
    
    public class MyService
    {
        [Inject]
        public ILogger Logger { get; set; }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var rel = relationships[0];
        rel.InjectionMethod.Should().Be(InjectionMethod.Property);
        rel.InjectionAttribute.Should().Be("Inject");
        rel.DependencyType.Should().Be("ILogger");
    }

    [TestMethod]
    public void Analyze_PropertyInjection_WithFromServicesAttribute_DetectsRelationship()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface IService { }
    
    public class FromServicesAttribute : System.Attribute { }
    
    public class MyController
    {
        [FromServices]
        public IService Service { get; set; }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var rel = relationships[0];
        rel.InjectionMethod.Should().Be(InjectionMethod.Property);
    }

    [TestMethod]
    public void Analyze_AddScopedRegistration_DetectsRelationship()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface IService { }
    public class ServiceImpl : IService { }
    
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddScoped<IService, ServiceImpl>();
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        var registration = relationships.FirstOrDefault(r => r.InjectionMethod == InjectionMethod.Registration);
        registration.Should().NotBeNull();
        registration!.ServiceType.Should().Be("IService");
        registration.ImplementationType.Should().Be("ServiceImpl");
        registration.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [TestMethod]
    public void Analyze_AddTransientRegistration_DetectsLifetime()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface IService { }
    public class ServiceImpl : IService { }
    
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddTransient<IService, ServiceImpl>();
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        var registration = relationships.FirstOrDefault(r => r.InjectionMethod == InjectionMethod.Registration);
        registration.Should().NotBeNull();
        registration!.Lifetime.Should().Be(ServiceLifetime.Transient);
    }

    [TestMethod]
    public void Analyze_AddSingletonRegistration_DetectsLifetime()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface ICache { }
    public class MemoryCache : ICache { }
    
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<ICache, MemoryCache>();
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        var registration = relationships.FirstOrDefault(r => r.InjectionMethod == InjectionMethod.Registration);
        registration.Should().NotBeNull();
        registration!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [TestMethod]
    public void Analyze_AddHostedService_DetectsRegistration()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class BackgroundWorker { }
    
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddHostedService<BackgroundWorker>();
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        var registration = relationships.FirstOrDefault(r => r.InjectionMethod == InjectionMethod.Registration);
        registration.Should().NotBeNull();
        registration!.ServiceType.Should().Be("BackgroundWorker");
        registration.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [TestMethod]
    public void Analyze_OptionalDependency_MarkedAsOptional()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface ILogger { }
    
    public class MyService
    {
        public MyService(ILogger? logger = null) { }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var rel = relationships[0];
        rel.IsOptional.Should().BeTrue();
    }

    [TestMethod]
    public void Analyze_ConcreteTypeDependency_NotMarkedAsInterface()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class ConcreteService { }

    public class MyService
    {
        public MyService(ConcreteService service) { }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var rel = relationships[0];
        rel.IsInterface.Should().BeFalse();
    }

    [TestMethod]
    public void Analyze_UnityRegisterType_DetectsRegistration()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface IService { }
    public class ServiceImpl : IService { }

    public class UnityConfig
    {
        public void Configure(IUnityContainer container)
        {
            container.RegisterType<IService, ServiceImpl>();
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        var registration = relationships.FirstOrDefault(r => r.InjectionMethod == InjectionMethod.Registration);
        registration.Should().NotBeNull();
        registration!.ServiceType.Should().Be("IService");
        registration.ImplementationType.Should().Be("ServiceImpl");
    }

    [TestMethod]
    public void Analyze_UnityRegisterSingleton_DetectsLifetime()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface ICache { }
    public class MemoryCache : ICache { }

    public class UnityConfig
    {
        public void Configure(IUnityContainer container)
        {
            container.RegisterSingleton<ICache, MemoryCache>();
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        var registration = relationships.FirstOrDefault(r => r.InjectionMethod == InjectionMethod.Registration);
        registration.Should().NotBeNull();
        registration!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [TestMethod]
    public void Analyze_UnityRegisterInstance_DetectsRegistration()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface ILogger { }
    public class ConsoleLogger : ILogger { }

    public class UnityConfig
    {
        public void Configure(IUnityContainer container)
        {
            container.RegisterInstance<ILogger>(new ConsoleLogger());
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        var registration = relationships.FirstOrDefault(r => r.InjectionMethod == InjectionMethod.Registration);
        registration.Should().NotBeNull();
        registration!.ServiceType.Should().Be("ILogger");
        registration.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [TestMethod]
    public void Analyze_PropertyInjection_WithDependencyAttribute_DetectsRelationship()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface ILogger { }

    public class DependencyAttribute : System.Attribute { }

    public class MyService
    {
        [Dependency]
        public ILogger Logger { get; set; }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var rel = relationships[0];
        rel.InjectionMethod.Should().Be(InjectionMethod.Property);
        rel.InjectionAttribute.Should().Be("Dependency");
        rel.DependencyType.Should().Be("ILogger");
    }

    [TestMethod]
    public void Analyze_PropertyInjection_WithOptionalDependencyAttribute_DetectsRelationship()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface ICache { }

    public class OptionalDependencyAttribute : System.Attribute { }

    public class MyService
    {
        [OptionalDependency]
        public ICache Cache { get; set; }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        relationships.Should().HaveCount(1);
        var rel = relationships[0];
        rel.InjectionMethod.Should().Be(InjectionMethod.Property);
        rel.InjectionAttribute.Should().Be("OptionalDependency");
    }

    [TestMethod]
    public void Analyze_UnityRegisterFactory_DetectsRegistration()
    {
        // Arrange
        var code = @"
namespace Test
{
    public interface IService { }

    public class UnityConfig
    {
        public void Configure(IUnityContainer container)
        {
            container.RegisterFactory<IService>(c => new ServiceImpl());
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var relationships = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        var registration = relationships.FirstOrDefault(r => r.InjectionMethod == InjectionMethod.Registration);
        registration.Should().NotBeNull();
        registration!.ServiceType.Should().Be("IService");
    }
}
