// -----------------------------------------------------------------------
// <copyright file="LspConfigurationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp.UnitTests;

[TestClass]
public class LspConfigurationTests
{
    [TestMethod]
    public void LspConfiguration_ShouldHaveDefaultValues()
    {
        // Act
        var config = new LspConfiguration();

        // Assert
        config.Enabled.Should().BeTrue();
        config.TimeoutMs.Should().Be(30000);
        config.MaxReferences.Should().Be(100);
        config.CollectDiagnostics.Should().BeTrue();
        config.CollectCallHierarchy.Should().BeTrue();
        config.ServerIdleTimeoutSeconds.Should().Be(300);
    }

    [TestMethod]
    public void CSharpLspConfiguration_ShouldHaveDefaultValues()
    {
        // Act
        var config = new CSharpLspConfiguration();

        // Assert
        config.Enabled.Should().BeTrue();
        config.ServerType.Should().Be(CSharpLspServerType.CSharpLs);
        config.ServerPath.Should().BeNull();
        config.SolutionPath.Should().BeNull();
        config.AdditionalArguments.Should().BeEmpty();
    }

    [TestMethod]
    public void PowerShellLspConfiguration_ShouldHaveDefaultValues()
    {
        // Act
        var config = new PowerShellLspConfiguration();

        // Assert
        config.Enabled.Should().BeTrue();
        config.PowerShellPath.Should().BeNull();
        config.EditorServicesPath.Should().BeNull();
        config.LogLevel.Should().Be("Normal");
        config.AdditionalArguments.Should().BeEmpty();
    }

    [TestMethod]
    public void EnrichmentProgress_ShouldCalculatePercentage()
    {
        // Arrange & Act
        var progress = new EnrichmentProgress(25, 100, "test.cs");

        // Assert
        progress.Percentage.Should().Be(25);
        progress.Processed.Should().Be(25);
        progress.Total.Should().Be(100);
        progress.CurrentFile.Should().Be("test.cs");
    }

    [TestMethod]
    public void EnrichmentProgress_ShouldHandleZeroTotal()
    {
        // Arrange & Act
        var progress = new EnrichmentProgress(0, 0, "test.cs");

        // Assert
        progress.Percentage.Should().Be(0);
    }

    [TestMethod]
    public void LspServerState_ShouldHaveAllStates()
    {
        // Assert - verify all expected states exist
        Enum.GetValues<LspServerState>().Should().HaveCount(6);
        Enum.IsDefined(LspServerState.NotStarted).Should().BeTrue();
        Enum.IsDefined(LspServerState.Starting).Should().BeTrue();
        Enum.IsDefined(LspServerState.Running).Should().BeTrue();
        Enum.IsDefined(LspServerState.Stopping).Should().BeTrue();
        Enum.IsDefined(LspServerState.Stopped).Should().BeTrue();
        Enum.IsDefined(LspServerState.Failed).Should().BeTrue();
    }

    [TestMethod]
    public void LspLanguage_ShouldHaveAllLanguages()
    {
        // Assert - verify all expected languages exist
        Enum.GetValues<LspLanguage>().Should().HaveCount(2);
        Enum.IsDefined(LspLanguage.CSharp).Should().BeTrue();
        Enum.IsDefined(LspLanguage.PowerShell).Should().BeTrue();
    }

    [TestMethod]
    public void CSharpLspServerType_ShouldHaveAllTypes()
    {
        // Assert - verify all expected server types exist
        Enum.GetValues<CSharpLspServerType>().Should().HaveCount(2);
        Enum.IsDefined(CSharpLspServerType.CSharpLs).Should().BeTrue();
        Enum.IsDefined(CSharpLspServerType.OmniSharp).Should().BeTrue();
    }
}
