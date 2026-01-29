// -----------------------------------------------------------------------
// <copyright file="MsBuildProjectParserTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Parsers;

using CodeIntelligence.Parsers;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="MsBuildProjectParser"/>.
/// </summary>
[TestClass]
public class MsBuildProjectParserTests
{
    private MsBuildProjectParser parser = null!;

    [TestInitialize]
    public void Setup()
    {
        this.parser = new MsBuildProjectParser();
    }

    [TestMethod]
    public void Parse_ProjectReference_ExtractsIncludePath()
    {
        // Arrange
        var content = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=""..\OtherProject\OtherProject.csproj"" />
  </ItemGroup>
</Project>";

        // Act
        var result = this.parser.Parse(@"C:\test\MyProject\MyProject.csproj", content);

        // Assert
        result.ProjectReferences.Should().HaveCount(1);
        var projRef = result.ProjectReferences[0];
        projRef.Include.Should().Be(@"..\OtherProject\OtherProject.csproj");
        projRef.ProjectName.Should().Be("OtherProject");
    }

    [TestMethod]
    public void Parse_MultipleProjectReferences_ExtractsAll()
    {
        // Arrange
        var content = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <ProjectReference Include=""..\Core\Core.csproj"" />
    <ProjectReference Include=""..\Common\Common.csproj"" />
    <ProjectReference Include=""..\Utils\Utils.csproj"" />
  </ItemGroup>
</Project>";

        // Act
        var result = this.parser.Parse(@"C:\test\MyProject\MyProject.csproj", content);

        // Assert
        result.ProjectReferences.Should().HaveCount(3);
        result.ProjectReferences.Select(p => p.ProjectName)
            .Should().Contain("Core", "Common", "Utils");
    }

    [TestMethod]
    public void Parse_PackageReference_ExtractsNameAndVersion()
    {
        // Arrange
        var content = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.3"" />
  </ItemGroup>
</Project>";

        // Act
        var result = this.parser.Parse(@"C:\test\MyProject.csproj", content);

        // Assert
        result.PackageReferences.Should().HaveCount(1);
        var pkgRef = result.PackageReferences[0];
        pkgRef.PackageName.Should().Be("Newtonsoft.Json");
        pkgRef.Version.Should().Be("13.0.3");
    }

    [TestMethod]
    public void Parse_PackageReferenceWithoutVersion_CentralPackageManagement()
    {
        // Arrange
        var content = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Microsoft.Extensions.Logging"" />
  </ItemGroup>
</Project>";

        // Act
        var result = this.parser.Parse(@"C:\test\MyProject.csproj", content);

        // Assert
        result.PackageReferences.Should().HaveCount(1);
        var pkgRef = result.PackageReferences[0];
        pkgRef.PackageName.Should().Be("Microsoft.Extensions.Logging");
        pkgRef.Version.Should().BeNull();
    }

    [TestMethod]
    public void Parse_ConditionalProjectReference_MarksAsConditional()
    {
        // Arrange
        var content = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup Condition=""'$(Configuration)' == 'Debug'"">
    <ProjectReference Include=""..\DebugTools\DebugTools.csproj"" Condition=""$(UseDebugTools)"" />
  </ItemGroup>
</Project>";

        // Act
        var result = this.parser.Parse(@"C:\test\MyProject.csproj", content);

        // Assert
        result.ProjectReferences.Should().HaveCount(1);
        result.ProjectReferences[0].IsConditional.Should().BeTrue();
    }

    [TestMethod]
    public void Parse_TargetFramework_Extracts()
    {
        // Arrange
        var content = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>";

        // Act
        var result = this.parser.Parse(@"C:\test\MyProject.csproj", content);

        // Assert
        result.TargetFramework.Should().Be("net8.0");
        result.TargetFrameworks.Should().Contain("net8.0");
    }

    [TestMethod]
    public void Parse_MultiTargetFrameworks_ExtractsAll()
    {
        // Arrange
        var content = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFrameworks>net6.0;net7.0;net8.0</TargetFrameworks>
  </PropertyGroup>
</Project>";

        // Act
        var result = this.parser.Parse(@"C:\test\MyProject.csproj", content);

        // Assert
        result.TargetFrameworks.Should().HaveCount(3);
        result.TargetFrameworks.Should().Contain("net6.0", "net7.0", "net8.0");
    }

    [TestMethod]
    public void Parse_OutputTypeExe_IsExecutable()
    {
        // Arrange
        var content = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>";

        // Act
        var result = this.parser.Parse(@"C:\test\MyProject.csproj", content);

        // Assert
        result.OutputType.Should().Be("Exe");
        result.IsExecutable.Should().BeTrue();
    }

    [TestMethod]
    public void Parse_LibraryProject_NotExecutable()
    {
        // Arrange
        var content = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>";

        // Act
        var result = this.parser.Parse(@"C:\test\MyProject.csproj", content);

        // Assert
        result.IsExecutable.Should().BeFalse();
    }

    [TestMethod]
    public void Parse_TestProject_IsTestProject()
    {
        // Arrange
        var content = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
</Project>";

        // Act
        var result = this.parser.Parse(@"C:\test\MyProject.csproj", content);

        // Assert
        result.IsTestProject.Should().BeTrue();
    }

    [TestMethod]
    public void Parse_Sdk_ExtractsValue()
    {
        // Arrange
        var content = @"
<Project Sdk=""Microsoft.NET.Sdk.Web"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>";

        // Act
        var result = this.parser.Parse(@"C:\test\MyProject.csproj", content);

        // Assert
        result.Sdk.Should().Be("Microsoft.NET.Sdk.Web");
    }

    [TestMethod]
    public void Parse_FrameworkReference_Extracts()
    {
        // Arrange
        var content = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <FrameworkReference Include=""Microsoft.AspNetCore.App"" />
  </ItemGroup>
</Project>";

        // Act
        var result = this.parser.Parse(@"C:\test\MyProject.csproj", content);

        // Assert
        result.FrameworkReferences.Should().HaveCount(1);
        result.FrameworkReferences[0].FrameworkName.Should().Be("Microsoft.AspNetCore.App");
    }

    [TestMethod]
    public void Parse_DevelopmentOnlyPackage_MarkedCorrectly()
    {
        // Arrange
        var content = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""coverlet.collector"" Version=""6.0.0"" PrivateAssets=""all"" />
  </ItemGroup>
</Project>";

        // Act
        var result = this.parser.Parse(@"C:\test\MyProject.csproj", content);

        // Assert
        result.PackageReferences.Should().HaveCount(1);
        result.PackageReferences[0].IsDevelopmentDependency.Should().BeTrue();
    }
}
