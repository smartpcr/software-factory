// -----------------------------------------------------------------------
// <copyright file="NormalizeForComparisonTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests.Sync;

using Ast.Memory.Sync;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class NormalizeForComparisonTests
{
    [TestMethod]
    public void StripsSingleLineComments()
    {
        var input = "int x = 1; // comment\nint y = 2;";
        var result = IncrementalSync.NormalizeForComparison(input, "C#");
        result.Should().Be("intx=1;inty=2;");
    }

    [TestMethod]
    public void StripsBlockComments()
    {
        var input = "int x = 1; /* block\ncomment */ int y = 2;";
        var result = IncrementalSync.NormalizeForComparison(input, "C#");
        result.Should().Be("intx=1;inty=2;");
    }

    [TestMethod]
    public void StripsXmlDocComments()
    {
        var input = "/// <summary>\n/// Doc comment\n/// </summary>\npublic void Foo() {}";
        var result = IncrementalSync.NormalizeForComparison(input, "C#");
        result.Should().Be("publicvoidFoo(){}");
    }

    [TestMethod]
    public void StripsPowerShellLineComments()
    {
        var input = "$x = 1 # comment\n$y = 2";
        var result = IncrementalSync.NormalizeForComparison(input, "PowerShell");
        result.Should().Be("$x=1$y=2");
    }

    [TestMethod]
    public void StripsPowerShellBlockComments()
    {
        var input = "$x = 1 <# block\ncomment #> $y = 2";
        var result = IncrementalSync.NormalizeForComparison(input, "PowerShell");
        result.Should().Be("$x=1$y=2");
    }

    [TestMethod]
    public void PreservesStringLiteralContent()
    {
        var input = "var s = \"hello // world\";";
        var result = IncrementalSync.NormalizeForComparison(input, "C#");
        result.Should().Be("vars=\"hello // world\";");
    }

    [TestMethod]
    public void PreservesVerbatimStringContent()
    {
        var input = "var s = @\"hello /* world */\";";
        var result = IncrementalSync.NormalizeForComparison(input, "C#");
        result.Should().Be("vars=@\"hello /* world */\";");
    }

    [TestMethod]
    public void StripsWhitespaceOnly()
    {
        var input = "int x = 1;";
        var spacey = "int   x  =  1 ;";
        IncrementalSync.NormalizeForComparison(input, "C#")
            .Should().Be(IncrementalSync.NormalizeForComparison(spacey, "C#"));
    }

    [TestMethod]
    public void WhitespaceAndCommentOnlyChangesProduceSameNormalization()
    {
        var original = "public void Foo() { Bar(); }";
        var modified = "public void Foo() { Bar(); } // added comment\n/* also this */";
        IncrementalSync.NormalizeForComparison(original, "C#")
            .Should().Be(IncrementalSync.NormalizeForComparison(modified, "C#"));
    }

    [TestMethod]
    public void SubstantiveChangeProducesDifferentNormalization()
    {
        var original = "public void Foo() { Bar(); }";
        var modified = "public void Foo() { Baz(); }";
        IncrementalSync.NormalizeForComparison(original, "C#")
            .Should().NotBe(IncrementalSync.NormalizeForComparison(modified, "C#"));
    }

    [TestMethod]
    public void CSharpPreprocessorDirectiveIsPreserved()
    {
        var input = "#region MyRegion\npublic void Foo() {}\n#endregion";
        var result = IncrementalSync.NormalizeForComparison(input, "C#");
        result.Should().Contain("#region");
    }

    [TestMethod]
    public void StringLiteralWhitespaceChangeIsSubstantive()
    {
        var original = "var s = \"hello world\";";
        var modified = "var s = \"hello  world\";";
        IncrementalSync.NormalizeForComparison(original, "C#")
            .Should().NotBe(IncrementalSync.NormalizeForComparison(modified, "C#"),
            "whitespace inside string literals is substantive content, not formatting");
    }
}
