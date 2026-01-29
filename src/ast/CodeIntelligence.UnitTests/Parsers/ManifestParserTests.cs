// -----------------------------------------------------------------------
// <copyright file="ManifestParserTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Parsers;

using CodeIntelligence.Parsers;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class ManifestParserTests
{
    private ManifestParser parser = null!;
    private string tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        this.parser = new ManifestParser();
        this.tempDir = Path.Combine(Path.GetTempPath(), $"ManifestParserTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, true);
        }
    }

    [TestMethod]
    public async Task ParseWinApplicationManifest_WithService_ExtractsServiceDefinition()
    {
        // Arrange
        var manifestPath = Path.Combine(this.tempDir, "WinApplicationManifest.xml");
        var manifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<ApplicationManifest>
    <Services>
        <Service Name=""MyWorkerService"">
            <AssemblyFile>MyWorker.dll</AssemblyFile>
        </Service>
    </Services>
</ApplicationManifest>";
        await File.WriteAllTextAsync(manifestPath, manifestContent);

        // Act
        var services = await this.parser.ParseWinApplicationManifestAsync(manifestPath);

        // Assert
        services.Should().HaveCountGreaterThan(0);
        services.Should().Contain(s => s.AssemblyFile == "MyWorker.dll");
    }

    [TestMethod]
    public async Task ParseServiceFabricManifest_WithStatelessService_ExtractsServiceInfo()
    {
        // Arrange
        var manifestPath = Path.Combine(this.tempDir, "ServiceManifest.xml");
        var manifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<ServiceManifest Name=""MyServicePkg"" Version=""1.0.0"">
    <ServiceTypes>
        <StatelessServiceType ServiceTypeName=""MyServiceType"" />
    </ServiceTypes>
    <CodePackage Name=""Code"" Version=""1.0.0"">
        <EntryPoint>
            <ExeHost>
                <Program>MyService.exe</Program>
            </ExeHost>
        </EntryPoint>
    </CodePackage>
</ServiceManifest>";
        await File.WriteAllTextAsync(manifestPath, manifestContent);

        // Act
        var services = await this.parser.ParseServiceFabricManifestAsync(manifestPath);

        // Assert
        services.Should().HaveCount(1);
        services[0].ServiceTypeName.Should().Be("MyServiceType");
        services[0].AssemblyFile.Should().Be("MyService.exe");
        services[0].IsStateless.Should().BeTrue();
        services[0].ManifestType.Should().Be(ManifestType.ServiceFabric);
    }

    [TestMethod]
    public async Task ParseServiceFabricManifest_WithStatefulService_DetectsStateful()
    {
        // Arrange
        var manifestPath = Path.Combine(this.tempDir, "ServiceManifest.xml");
        var manifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<ServiceManifest Name=""StatefulPkg"" Version=""2.0.0"">
    <ServiceTypes>
        <StatefulServiceType ServiceTypeName=""StatefulWorkerType"" />
    </ServiceTypes>
    <CodePackage Name=""Code"" Version=""2.0.0"">
        <EntryPoint>
            <ExeHost>
                <Program>StatefulWorker.exe</Program>
            </ExeHost>
        </EntryPoint>
    </CodePackage>
</ServiceManifest>";
        await File.WriteAllTextAsync(manifestPath, manifestContent);

        // Act
        var services = await this.parser.ParseServiceFabricManifestAsync(manifestPath);

        // Assert
        services.Should().HaveCount(1);
        services[0].IsStateless.Should().BeFalse();
    }

    [TestMethod]
    public async Task ParseSettingsXml_WithServiceAssembly_ExtractsHostSetting()
    {
        // Arrange
        var settingsPath = Path.Combine(this.tempDir, "Settings.xml");
        var settingsContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Settings>
    <Section Name=""HostLoadConfigSection"">
        <Parameter Name=""ServiceAssembly"" Value=""MyService.dll"" />
        <Parameter Name=""ServiceType"" Value=""MyServiceType"" />
    </Section>
</Settings>";
        await File.WriteAllTextAsync(settingsPath, settingsContent);

        // Act
        var settings = await this.parser.ParseSettingsXmlAsync(settingsPath);

        // Assert
        settings.Should().HaveCount(2);
        settings.Should().Contain(s => s.IsServiceAssembly && s.Value == "MyService.dll");
    }

    [TestMethod]
    public async Task ParseProjectSdk_WorkerSdk_DetectsWorkerProject()
    {
        // Arrange
        var projectPath = Path.Combine(this.tempDir, "MyWorker.csproj");
        var projectContent = @"<Project Sdk=""Microsoft.NET.Sdk.Worker"">
    <PropertyGroup>
        <TargetFramework>net8.0</TargetFramework>
        <OutputType>Exe</OutputType>
    </PropertyGroup>
</Project>";
        await File.WriteAllTextAsync(projectPath, projectContent);

        // Act
        var sdkInfo = await this.parser.ParseProjectSdkAsync(projectPath);

        // Assert
        sdkInfo.Should().NotBeNull();
        sdkInfo!.Sdk.Should().Be("Microsoft.NET.Sdk.Worker");
        sdkInfo.ProjectType.Should().Be(ProjectType.Worker);
        sdkInfo.IsWorkerProject.Should().BeTrue();
        sdkInfo.IsExecutable.Should().BeTrue();
    }

    [TestMethod]
    public async Task ParseProjectSdk_WebSdk_DetectsWebProject()
    {
        // Arrange
        var projectPath = Path.Combine(this.tempDir, "MyWebApp.csproj");
        var projectContent = @"<Project Sdk=""Microsoft.NET.Sdk.Web"">
    <PropertyGroup>
        <TargetFramework>net8.0</TargetFramework>
    </PropertyGroup>
</Project>";
        await File.WriteAllTextAsync(projectPath, projectContent);

        // Act
        var sdkInfo = await this.parser.ParseProjectSdkAsync(projectPath);

        // Assert
        sdkInfo.Should().NotBeNull();
        sdkInfo!.ProjectType.Should().Be(ProjectType.AspNetCore);
        sdkInfo.IsWebProject.Should().BeTrue();
    }

    [TestMethod]
    public async Task ParseProjectSdk_ConsoleSdk_DetectsConsoleProject()
    {
        // Arrange
        var projectPath = Path.Combine(this.tempDir, "MyConsole.csproj");
        var projectContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
    <PropertyGroup>
        <TargetFramework>net8.0</TargetFramework>
        <OutputType>Exe</OutputType>
    </PropertyGroup>
</Project>";
        await File.WriteAllTextAsync(projectPath, projectContent);

        // Act
        var sdkInfo = await this.parser.ParseProjectSdkAsync(projectPath);

        // Assert
        sdkInfo.Should().NotBeNull();
        sdkInfo!.ProjectType.Should().Be(ProjectType.Console);
        sdkInfo.IsExecutable.Should().BeTrue();
    }

    [TestMethod]
    public async Task ParseProjectSdk_LibrarySdk_DetectsLibrary()
    {
        // Arrange
        var projectPath = Path.Combine(this.tempDir, "MyLib.csproj");
        var projectContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
    <PropertyGroup>
        <TargetFramework>net8.0</TargetFramework>
        <OutputType>Library</OutputType>
    </PropertyGroup>
</Project>";
        await File.WriteAllTextAsync(projectPath, projectContent);

        // Act
        var sdkInfo = await this.parser.ParseProjectSdkAsync(projectPath);

        // Assert
        sdkInfo.Should().NotBeNull();
        sdkInfo!.ProjectType.Should().Be(ProjectType.Library);
        sdkInfo.IsExecutable.Should().BeFalse();
    }

    [TestMethod]
    public async Task ParseManifest_AutoDetectsType()
    {
        // Arrange
        var manifestPath = Path.Combine(this.tempDir, "WinApplicationManifest.xml");
        var manifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<ApplicationManifest>
    <Services>
        <Service Name=""TestService"">
            <AssemblyFile>Test.dll</AssemblyFile>
        </Service>
    </Services>
</ApplicationManifest>";
        await File.WriteAllTextAsync(manifestPath, manifestContent);

        // Act
        var manifest = await this.parser.ParseManifestAsync(manifestPath);

        // Assert
        manifest.Should().NotBeNull();
        manifest!.Type.Should().Be(ManifestType.WinApplication);
    }

    [TestMethod]
    public async Task ParseManifest_InvalidFile_ReturnsNull()
    {
        // Arrange
        var filePath = Path.Combine(this.tempDir, "unknown.txt");
        await File.WriteAllTextAsync(filePath, "not a manifest");

        // Act
        var manifest = await this.parser.ParseManifestAsync(filePath);

        // Assert
        manifest.Should().BeNull();
    }

    [TestMethod]
    public async Task FindProjectManifests_WithCopyToOutput_FindsManifests()
    {
        // Arrange - Create a project with manifest references
        var projectDir = Path.Combine(this.tempDir, "MyService");
        Directory.CreateDirectory(projectDir);

        var csprojPath = Path.Combine(projectDir, "MyService.csproj");
        var projectContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net8.0</TargetFramework>
    </PropertyGroup>
    <ItemGroup>
        <Content Include=""WinApplicationManifest.xml"">
            <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
        </Content>
        <Content Include=""Settings.xml"">
            <CopyToOutputDirectory>Always</CopyToOutputDirectory>
        </Content>
    </ItemGroup>
</Project>";
        await File.WriteAllTextAsync(csprojPath, projectContent);

        // Create the manifest files
        var manifestContent = @"<?xml version=""1.0""?><ApplicationManifest><Services/></ApplicationManifest>";
        await File.WriteAllTextAsync(Path.Combine(projectDir, "WinApplicationManifest.xml"), manifestContent);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "Settings.xml"), @"<?xml version=""1.0""?><Settings/>");

        // Act
        var manifests = await this.parser.FindProjectManifestsAsync(csprojPath);

        // Assert
        manifests.Should().HaveCount(2);
        manifests.Should().Contain(m => m.ManifestType == ManifestType.WinApplication);
        manifests.Should().Contain(m => m.ManifestType == ManifestType.Settings);

        var winManifest = manifests.First(m => m.ManifestType == ManifestType.WinApplication);
        winManifest.CopyAction.Should().Be("PreserveNewest");
        winManifest.ItemType.Should().Be("Content");
        winManifest.RelativePath.Should().Be("WinApplicationManifest.xml");
    }

    [TestMethod]
    public async Task FindProjectManifests_WithRelativePath_ResolvesCorrectly()
    {
        // Arrange - Create project with relative path manifest reference
        var projectDir = Path.Combine(this.tempDir, "src", "MyService");
        Directory.CreateDirectory(projectDir);

        var csprojPath = Path.Combine(projectDir, "MyService.csproj");
        var projectContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
    </PropertyGroup>
    <ItemGroup>
        <Content Include="".\Config\WinApplicationManifest.xml"">
            <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
        </Content>
    </ItemGroup>
</Project>";
        await File.WriteAllTextAsync(csprojPath, projectContent);

        // Create manifest in subfolder
        var configDir = Path.Combine(projectDir, "Config");
        Directory.CreateDirectory(configDir);
        var manifestContent = @"<?xml version=""1.0""?><ApplicationManifest><Services/></ApplicationManifest>";
        await File.WriteAllTextAsync(Path.Combine(configDir, "WinApplicationManifest.xml"), manifestContent);

        // Act
        var manifests = await this.parser.FindProjectManifestsAsync(csprojPath);

        // Assert
        manifests.Should().HaveCount(1);
        var manifest = manifests[0];
        manifest.ManifestType.Should().Be(ManifestType.WinApplication);
        manifest.ManifestPath.Should().EndWith("WinApplicationManifest.xml");
        File.Exists(manifest.ManifestPath).Should().BeTrue();
    }

    [TestMethod]
    public async Task FindProjectManifests_NoManifests_ReturnsEmpty()
    {
        // Arrange
        var projectDir = Path.Combine(this.tempDir, "LibraryProject");
        Directory.CreateDirectory(projectDir);

        var csprojPath = Path.Combine(projectDir, "MyLibrary.csproj");
        var projectContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
    <PropertyGroup>
        <TargetFramework>net8.0</TargetFramework>
    </PropertyGroup>
</Project>";
        await File.WriteAllTextAsync(csprojPath, projectContent);

        // Act
        var manifests = await this.parser.FindProjectManifestsAsync(csprojPath);

        // Assert
        manifests.Should().BeEmpty();
    }

    [TestMethod]
    public void FindManifestFiles_FindsManifestsInDirectory()
    {
        // Arrange - Create manifest files in temp directory
        var manifestDir = Path.Combine(this.tempDir, "manifests");
        Directory.CreateDirectory(manifestDir);

        File.WriteAllText(Path.Combine(manifestDir, "WinApplicationManifest.xml"), "<xml/>");
        File.WriteAllText(Path.Combine(manifestDir, "Settings.xml"), "<xml/>");

        // Act
        var manifests = this.parser.FindManifestFiles(manifestDir);

        // Assert
        manifests.Should().HaveCount(2);
        manifests.Should().Contain(m => m.EndsWith("WinApplicationManifest.xml"));
        manifests.Should().Contain(m => m.EndsWith("Settings.xml"));
    }

    [TestMethod]
    public async Task GetEntryPointManifests_FindsAssociatedManifests()
    {
        // Arrange - Create a project with an entry point and manifest
        var projectDir = Path.Combine(this.tempDir, "WinService");
        Directory.CreateDirectory(projectDir);

        // Create .csproj with manifest reference
        var csprojPath = Path.Combine(projectDir, "WinService.csproj");
        var projectContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
    </PropertyGroup>
    <ItemGroup>
        <Content Include=""WinApplicationManifest.xml"">
            <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
        </Content>
    </ItemGroup>
</Project>";
        await File.WriteAllTextAsync(csprojPath, projectContent);

        // Create entry point source file
        var entryPointPath = Path.Combine(projectDir, "MyWinService.cs");
        await File.WriteAllTextAsync(entryPointPath, "public class MyWinService : ServiceBase { }");

        // Create manifest
        var manifestContent = @"<?xml version=""1.0""?>
<ApplicationManifest>
    <Services>
        <Service Name=""MyWinService"">
            <AssemblyFile>MyWinService.dll</AssemblyFile>
        </Service>
    </Services>
</ApplicationManifest>";
        await File.WriteAllTextAsync(Path.Combine(projectDir, "WinApplicationManifest.xml"), manifestContent);

        // Act
        var manifests = await this.parser.GetEntryPointManifestsAsync(entryPointPath);

        // Assert
        manifests.Should().NotBeEmpty();
        manifests.Should().Contain(m => m.Type == ManifestType.WinApplication);

        var winManifest = manifests.First(m => m.Type == ManifestType.WinApplication);
        winManifest.Services.Should().NotBeEmpty();
        winManifest.Services.Should().Contain(s =>
            s.ServiceName == "MyWinService" &&
            s.AssemblyFile == "MyWinService.dll");
    }
}
