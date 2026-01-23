// -----------------------------------------------------------------------
// <copyright file="LspServerManagerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp.UnitTests;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[TestClass]
public class LspServerManagerTests
{
    [TestMethod]
    public void GetLanguageForFile_ShouldReturnCSharp_ForCsFiles()
    {
        // Assert
        LspServerManager.GetLanguageForFile("test.cs").Should().Be(LspLanguage.CSharp);
        LspServerManager.GetLanguageForFile("Test.CS").Should().Be(LspLanguage.CSharp);
        LspServerManager.GetLanguageForFile(@"C:\path\to\MyClass.cs").Should().Be(LspLanguage.CSharp);
    }

    [TestMethod]
    public void GetLanguageForFile_ShouldReturnCSharp_ForCsxFiles()
    {
        // Assert
        LspServerManager.GetLanguageForFile("script.csx").Should().Be(LspLanguage.CSharp);
    }

    [TestMethod]
    public void GetLanguageForFile_ShouldReturnPowerShell_ForPs1Files()
    {
        // Assert
        LspServerManager.GetLanguageForFile("script.ps1").Should().Be(LspLanguage.PowerShell);
        LspServerManager.GetLanguageForFile("Script.PS1").Should().Be(LspLanguage.PowerShell);
        LspServerManager.GetLanguageForFile(@"C:\scripts\Deploy.ps1").Should().Be(LspLanguage.PowerShell);
    }

    [TestMethod]
    public void GetLanguageForFile_ShouldReturnPowerShell_ForPsm1Files()
    {
        // Assert
        LspServerManager.GetLanguageForFile("module.psm1").Should().Be(LspLanguage.PowerShell);
    }

    [TestMethod]
    public void GetLanguageForFile_ShouldReturnPowerShell_ForPsd1Files()
    {
        // Assert
        LspServerManager.GetLanguageForFile("manifest.psd1").Should().Be(LspLanguage.PowerShell);
    }

    [TestMethod]
    public void GetLanguageForFile_ShouldReturnNull_ForUnsupportedFiles()
    {
        // Assert
        LspServerManager.GetLanguageForFile("file.js").Should().BeNull();
        LspServerManager.GetLanguageForFile("file.py").Should().BeNull();
        LspServerManager.GetLanguageForFile("file.txt").Should().BeNull();
        LspServerManager.GetLanguageForFile("file").Should().BeNull();
    }

    [TestMethod]
    public void GetClient_ShouldReturnNull_WhenClientDoesNotExist()
    {
        // Arrange
        var options = Options.Create(new LspConfiguration());
        var loggerFactory = LoggerFactory.Create(builder => { });
        var manager = new LspServerManager(options, loggerFactory);

        // Act
        var client = manager.GetClient(LspLanguage.CSharp, @"C:\test\workspace");

        // Assert
        client.Should().BeNull();
    }

    [TestMethod]
    public void GetActiveClients_ShouldReturnEmptyCollection_WhenNoClients()
    {
        // Arrange
        var options = Options.Create(new LspConfiguration());
        var loggerFactory = LoggerFactory.Create(builder => { });
        var manager = new LspServerManager(options, loggerFactory);

        // Act
        var clients = manager.GetActiveClients();

        // Assert
        clients.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ShutdownAllAsync_ShouldNotThrow_WhenNoClients()
    {
        // Arrange
        var options = Options.Create(new LspConfiguration());
        var loggerFactory = LoggerFactory.Create(builder => { });
        var manager = new LspServerManager(options, loggerFactory);

        // Act & Assert
        await manager.Invoking(m => m.ShutdownAllAsync()).Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task DisposeAsync_ShouldNotThrow_WhenNoClients()
    {
        // Arrange
        var options = Options.Create(new LspConfiguration());
        var loggerFactory = LoggerFactory.Create(builder => { });
        var manager = new LspServerManager(options, loggerFactory);

        // Act & Assert
        await manager.Invoking(m => m.DisposeAsync().AsTask()).Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task GetOrCreateClientAsync_ShouldThrow_WhenLanguageDisabled()
    {
        // Arrange
        var config = new LspConfiguration
        {
            CSharp = { Enabled = false }
        };
        var options = Options.Create(config);
        var loggerFactory = LoggerFactory.Create(builder => { });
        var manager = new LspServerManager(options, loggerFactory);

        // Act & Assert
        await manager.Invoking(m => m.GetOrCreateClientAsync(LspLanguage.CSharp, @"C:\test"))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not enabled*");
    }

    [TestMethod]
    public async Task GetOrCreateClientAsync_ShouldThrow_WhenLspDisabled()
    {
        // Arrange
        var config = new LspConfiguration { Enabled = false };
        var options = Options.Create(config);
        var loggerFactory = LoggerFactory.Create(builder => { });
        var manager = new LspServerManager(options, loggerFactory);

        // Act & Assert
        await manager.Invoking(m => m.GetOrCreateClientAsync(LspLanguage.CSharp, @"C:\test"))
            .Should().ThrowAsync<InvalidOperationException>();
    }
}
