// -----------------------------------------------------------------------
// <copyright file="CanonicalIdTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.Memory.Extraction;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class CanonicalIdTests
{
    [TestMethod]
    public void ForFile_ShouldReturnSlashPrefixedPath()
    {
        var id = CanonicalId.ForFile("src/Services/PaymentService.cs");
        id.Should().Be("/src/Services/PaymentService.cs");
    }

    [TestMethod]
    public void ForFile_ShouldNormalizeBackslashes()
    {
        var id = CanonicalId.ForFile("src\\Services\\PaymentService.cs");
        id.Should().Be("/src/Services/PaymentService.cs");
    }

    [TestMethod]
    public void ForSymbol_ShouldReturnPathDoubleColonQualifiedName()
    {
        var id = CanonicalId.ForSymbol("src/Services/PaymentService.cs", "MyApp.Services.PaymentService");
        id.Should().Be("/src/Services/PaymentService.cs::MyApp.Services.PaymentService");
    }

    [TestMethod]
    public void ForPackage_ShouldReturnProjectDirectory()
    {
        var id = CanonicalId.ForPackage("src/Services/Services.csproj");
        id.Should().Be("/src/Services");
    }

    [TestMethod]
    public void RepoRoot_ShouldBeSlash()
    {
        CanonicalId.RepoRoot.Should().Be("/");
    }

    [TestMethod]
    public void ExtractQualifiedName_ShouldReturnNameAfterDoubleColon()
    {
        var name = CanonicalId.ExtractQualifiedName("/src/Foo.cs::MyApp.Foo");
        name.Should().Be("MyApp.Foo");
    }

    [TestMethod]
    public void ExtractQualifiedName_ShouldReturnNullForFilePath()
    {
        var name = CanonicalId.ExtractQualifiedName("/src/Foo.cs");
        name.Should().BeNull();
    }

    [TestMethod]
    public void ExtractFilePath_ShouldReturnPathBeforeDoubleColon()
    {
        var path = CanonicalId.ExtractFilePath("/src/Foo.cs::MyApp.Foo");
        path.Should().Be("src/Foo.cs");
    }

    [TestMethod]
    public void ExtractFilePath_ShouldReturnFullPathForFileId()
    {
        var path = CanonicalId.ExtractFilePath("/src/Foo.cs");
        path.Should().Be("src/Foo.cs");
    }

    [TestMethod]
    public void IsInRepo_ShouldReturnTrueForSlashPrefix()
    {
        CanonicalId.IsInRepo("/src/Foo.cs").Should().BeTrue();
    }

    [TestMethod]
    public void IsExternal_ShouldReturnTrueForExtPrefix()
    {
        CanonicalId.IsExternal("ext:Newtonsoft.Json:JsonConvert").Should().BeTrue();
    }

    [TestMethod]
    public void NormalizePath_ShouldStripLeadingSlashAndDotSlash()
    {
        CanonicalId.NormalizePath("./src/Foo.cs").Should().Be("src/Foo.cs");
        CanonicalId.NormalizePath("/src/Foo.cs").Should().Be("src/Foo.cs");
    }
}