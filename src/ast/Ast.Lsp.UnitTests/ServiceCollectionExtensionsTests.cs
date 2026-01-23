// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensionsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp.UnitTests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[TestClass]
public class ServiceCollectionExtensionsTests
{
    [TestMethod]
    public void AddLspServices_ShouldRegisterRequiredServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddLspServices();
        var provider = services.BuildServiceProvider();

        // Assert
        provider.GetService<LspServerManager>().Should().NotBeNull();
        provider.GetService<LspEnrichmentService>().Should().NotBeNull();
        provider.GetService<IOptions<LspConfiguration>>().Should().NotBeNull();
    }

    [TestMethod]
    public void AddLspServices_ShouldApplyConfiguration()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddLspServices(config =>
        {
            config.Enabled = false;
            config.TimeoutMs = 60000;
            config.MaxReferences = 50;
        });
        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetRequiredService<IOptions<LspConfiguration>>();
        options.Value.Enabled.Should().BeFalse();
        options.Value.TimeoutMs.Should().Be(60000);
        options.Value.MaxReferences.Should().Be(50);
    }

    [TestMethod]
    public void AddCSharpLsp_ShouldConfigureCSharpOnly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddCSharpLsp(CSharpLspServerType.OmniSharp, "/path/to/omnisharp");
        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetRequiredService<IOptions<LspConfiguration>>();
        options.Value.CSharp.Enabled.Should().BeTrue();
        options.Value.CSharp.ServerType.Should().Be(CSharpLspServerType.OmniSharp);
        options.Value.CSharp.ServerPath.Should().Be("/path/to/omnisharp");
        options.Value.PowerShell.Enabled.Should().BeFalse();
    }

    [TestMethod]
    public void AddPowerShellLsp_ShouldConfigurePowerShellOnly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddPowerShellLsp("/usr/bin/pwsh", "/path/to/pses");
        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetRequiredService<IOptions<LspConfiguration>>();
        options.Value.CSharp.Enabled.Should().BeFalse();
        options.Value.PowerShell.Enabled.Should().BeTrue();
        options.Value.PowerShell.PowerShellPath.Should().Be("/usr/bin/pwsh");
        options.Value.PowerShell.EditorServicesPath.Should().Be("/path/to/pses");
    }

    [TestMethod]
    public void AddAllLspServices_ShouldEnableBothLanguages()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddAllLspServices();
        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetRequiredService<IOptions<LspConfiguration>>();
        options.Value.CSharp.Enabled.Should().BeTrue();
        options.Value.PowerShell.Enabled.Should().BeTrue();
    }

    [TestMethod]
    public void AddLspServices_ShouldRegisterAsSingletons()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLspServices();
        var provider = services.BuildServiceProvider();

        // Act
        var manager1 = provider.GetRequiredService<LspServerManager>();
        var manager2 = provider.GetRequiredService<LspServerManager>();
        var service1 = provider.GetRequiredService<LspEnrichmentService>();
        var service2 = provider.GetRequiredService<LspEnrichmentService>();

        // Assert
        manager1.Should().BeSameAs(manager2);
        service1.Should().BeSameAs(service2);
    }

    [TestMethod]
    public void AddLspServices_ShouldConfigureCSharpAdditionalArguments()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddLspServices(config =>
        {
            config.CSharp.AdditionalArguments = ["--debug", "--verbose"];
        });
        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetRequiredService<IOptions<LspConfiguration>>();
        options.Value.CSharp.AdditionalArguments.Should().Contain("--debug");
        options.Value.CSharp.AdditionalArguments.Should().Contain("--verbose");
    }

    [TestMethod]
    public void AddLspServices_ShouldConfigurePowerShellLogLevel()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddLspServices(config =>
        {
            config.PowerShell.LogLevel = "Diagnostic";
        });
        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetRequiredService<IOptions<LspConfiguration>>();
        options.Value.PowerShell.LogLevel.Should().Be("Diagnostic");
    }
}
