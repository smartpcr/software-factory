// -----------------------------------------------------------------------
// <copyright file="PowerShellHyperedgeExtractorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.PowerShell.UnitTests;

using Ast.Core;

[TestClass]
public class PowerShellHyperedgeExtractorTests
{
    [TestMethod]
    public void Extract_ImportModule_CreatesModuleImportHyperedge()
    {
        // Arrange
        var code = @"Import-Module ActiveDirectory";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.ModuleImport);
        var importEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.ModuleImport);
        importEdge.Metadata["module_name"].Should().Be("ActiveDirectory");
    }

    [TestMethod]
    public void Extract_ImportModuleWithVersion_CapturesVersion()
    {
        // Arrange
        var code = @"Import-Module Az.Storage -RequiredVersion 5.0.0";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var importEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.ModuleImport);
        importEdge.Metadata["module_name"].Should().Be("Az.Storage");
        importEdge.Metadata["version"].Should().Be("5.0.0");
    }

    [TestMethod]
    public void Extract_ImportModuleWithPrefix_CapturesPrefix()
    {
        // Arrange
        var code = @"Import-Module ActiveDirectory -Prefix AD";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var importEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.ModuleImport);
        importEdge.Metadata["prefix"].Should().Be("AD");
        nodes.Values.Should().Contain(n => n.NodeType == "prefix" && n.Name == "AD");
    }

    [TestMethod]
    public void Extract_ImportModuleGlobal_CapturesGlobalFlag()
    {
        // Arrange
        var code = @"Import-Module PSReadLine -Global";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var importEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.ModuleImport);
        importEdge.Metadata["is_global"].Should().Be(true);
    }

    [TestMethod]
    public void Extract_ModuleQualifiedCall_CreatesModuleFunctionCallHyperedge()
    {
        // Arrange
        var code = @"Microsoft.PowerShell.Management\Get-ChildItem -Path C:\";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.ModuleFunctionCall);
        var callEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.ModuleFunctionCall);
        callEdge.Metadata["module_name"].Should().Be("Microsoft.PowerShell.Management");
        callEdge.Metadata["function_name"].Should().Be("Get-ChildItem");
    }

    [TestMethod]
    public void Extract_ModuleQualifiedCallWithParams_CapturesAllParameters()
    {
        // Arrange
        var code = @"ActiveDirectory\Get-ADUser -Filter * -SearchBase 'OU=Users,DC=corp,DC=com'";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var callEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.ModuleFunctionCall);
        callEdge.Roles.Values.Should().Contain("source_module");
        callEdge.Roles.Values.Should().Contain("called_function");
        callEdge.Roles.Values.Should().Contain("param_Filter");
        callEdge.Roles.Values.Should().Contain("param_SearchBase");
    }

    [TestMethod]
    public void Extract_InvokeCommand_CreatesRemoteExecutionHyperedge()
    {
        // Arrange
        var code = @"Invoke-Command -ComputerName Server01 -ScriptBlock { Get-Process }";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.RemoteExecution);
        var remoteEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.RemoteExecution);
        remoteEdge.Metadata["cmdlet"].Should().Be("Invoke-Command");
        remoteEdge.Metadata["computer_name"].Should().Be("Server01");
        remoteEdge.Metadata["has_script_block"].Should().Be(true);
    }

    [TestMethod]
    public void Extract_InvokeCommandMultipleComputers_CapturesAllTargets()
    {
        // Arrange
        var code = @"Invoke-Command -ComputerName Server01, Server02, Server03 -ScriptBlock { hostname }";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var remoteEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.RemoteExecution);
        remoteEdge.Roles.Values.Should().Contain("target_computer_0");
        remoteEdge.Roles.Values.Should().Contain("target_computer_1");
        remoteEdge.Roles.Values.Should().Contain("target_computer_2");
    }

    [TestMethod]
    public void Extract_InvokeCommandWithCredential_CapturesCredential()
    {
        // Arrange
        var code = @"Invoke-Command -ComputerName DC01 -Credential $cred -ScriptBlock { whoami }";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var remoteEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.RemoteExecution);
        remoteEdge.Metadata["credential"].Should().Be("$cred");
        nodes.Values.Should().Contain(n => n.NodeType == "credential");
    }

    [TestMethod]
    public void Extract_InvokeCommandWithSession_CapturesSession()
    {
        // Arrange
        var code = @"Invoke-Command -Session $session -ScriptBlock { Get-Service }";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var remoteEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.RemoteExecution);
        remoteEdge.Metadata["session"].Should().Be("$session");
        remoteEdge.Roles.Values.Should().Contain("session");
    }

    [TestMethod]
    public void Extract_InvokeCommandWithFilePath_CapturesScriptFile()
    {
        // Arrange
        var code = @"Invoke-Command -ComputerName Server01 -FilePath C:\Scripts\Deploy.ps1";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var remoteEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.RemoteExecution);
        remoteEdge.Metadata["has_script_file"].Should().Be(true);
        remoteEdge.Roles.Values.Should().Contain("script_file");
    }

    [TestMethod]
    public void Extract_InvokeCommandWithSSL_CapturesSecuritySettings()
    {
        // Arrange
        var code = @"Invoke-Command -ComputerName Server01 -UseSSL -Port 5986 -ScriptBlock { hostname }";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var remoteEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.RemoteExecution);
        remoteEdge.Metadata["use_ssl"].Should().Be(true);
        remoteEdge.Metadata["port"].Should().Be(5986);
    }

    [TestMethod]
    public void Extract_NewPSSession_CreatesRemoteExecutionHyperedge()
    {
        // Arrange
        var code = @"New-PSSession -ComputerName DC01 -Credential (Get-Credential)";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.RemoteExecution);
        var remoteEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.RemoteExecution);
        remoteEdge.Metadata["cmdlet"].Should().Be("New-PSSession");
    }

    [TestMethod]
    public void Extract_EnterPSSession_CreatesRemoteExecutionHyperedge()
    {
        // Arrange
        var code = @"Enter-PSSession -ComputerName WebServer01 -Authentication Kerberos";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var remoteEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.RemoteExecution);
        remoteEdge.Metadata["cmdlet"].Should().Be("Enter-PSSession");
        remoteEdge.Metadata["authentication"].Should().Be("Kerberos");
    }

    [TestMethod]
    public void Extract_InvokeCommandAsJob_CapturesJobFlag()
    {
        // Arrange
        var code = @"Invoke-Command -ComputerName Server01 -ScriptBlock { Start-Sleep 60 } -AsJob";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        var remoteEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.RemoteExecution);
        remoteEdge.Metadata["as_job"].Should().Be(true);
    }

    [TestMethod]
    public void Extract_RemovePSSession_TracksSessionCleanup()
    {
        // Arrange
        var code = @"Remove-PSSession -Session $session";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.RemoteExecution);
        var remoteEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.RemoteExecution);
        remoteEdge.Metadata["cmdlet"].Should().Be("Remove-PSSession");
    }

    [TestMethod]
    public void Extract_CompleteRemoteWorkflow_TracksAllRemoteCalls()
    {
        // Arrange
        var code = @"
$cred = Get-Credential
$session = New-PSSession -ComputerName DC01 -Credential $cred
Invoke-Command -Session $session -FilePath .\Deploy.ps1
Remove-PSSession -Session $session";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert - should have 3 remote execution hyperedges
        var remoteEdges = hyperedges.Where(e => e.EdgeType == HyperedgeType.RemoteExecution).ToList();
        remoteEdges.Should().HaveCount(3);
        remoteEdges.Should().Contain(e => e.Metadata["cmdlet"]!.ToString() == "New-PSSession");
        remoteEdges.Should().Contain(e => e.Metadata["cmdlet"]!.ToString() == "Invoke-Command");
        remoteEdges.Should().Contain(e => e.Metadata["cmdlet"]!.ToString() == "Remove-PSSession");
    }

    [TestMethod]
    public void Extract_FunctionDefinition_CreatesFunctionDefinitionHyperedge()
    {
        // Arrange
        var code = @"
function Get-UserInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Username
    )
    Get-ADUser -Identity $Username
}";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.FunctionDefinition);
        var funcEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.FunctionDefinition);
        funcEdge.Roles.Values.Should().Contain("function");
        funcEdge.Roles.Values.Should().Contain("param_0");
    }

    [TestMethod]
    public void Extract_Pipeline_CreatesPipelineHyperedge()
    {
        // Arrange
        var code = @"Get-Process | Where-Object { $_.CPU -gt 100 } | Sort-Object CPU -Descending";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var (nodes, hyperedges) = extractor.Extract(code);

        // Assert
        hyperedges.Should().Contain(e => e.EdgeType == HyperedgeType.Pipeline);
        var pipelineEdge = hyperedges.First(e => e.EdgeType == HyperedgeType.Pipeline);
        pipelineEdge.Roles.Values.Should().Contain("stage_0");
        pipelineEdge.Roles.Values.Should().Contain("stage_1");
        pipelineEdge.Roles.Values.Should().Contain("stage_2");
    }

    [TestMethod]
    public void ExtractToHypergraph_ReturnsCompleteHypergraph()
    {
        // Arrange
        var code = @"
Import-Module ActiveDirectory
Get-ADUser -Filter * | Export-Csv users.csv";
        var extractor = new PowerShellHyperedgeExtractor("test.ps1");

        // Act
        var hypergraph = extractor.ExtractToHypergraph(code);

        // Assert
        hypergraph.Nodes.Should().NotBeEmpty();
        hypergraph.Hyperedges.Should().NotBeEmpty();
        hypergraph.Metadata.NumPowerShellNodes.Should().BeGreaterThan(0);
        hypergraph.Metadata.Files.Should().Contain("test.ps1");
    }
}