// -----------------------------------------------------------------------
// <copyright file="TestAttributeDetector.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.ImpactedTests.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Phase 59 Stage C — recognises test-method attributes from the three
/// major .NET test frameworks. Walks a method's attribute list
/// syntactically (no semantic-model resolution needed — attribute
/// names are stable across frameworks, and we don't care about the
/// exact namespace) so the detector is cheap to call per reference
/// location.
///
/// <para>Match is by the attribute's short name, stripped of an
/// optional <c>Attribute</c> suffix. <c>[FactAttribute]</c>,
/// <c>[Fact]</c>, <c>[Xunit.Fact]</c>, <c>[global::Xunit.Fact]</c>
/// all resolve to <c>Fact</c>. Fully-qualified vs. simple names work
/// either way because we look at the last identifier in a
/// dotted name.</para>
/// </summary>
public static class TestAttributeDetector
{
    /// <summary>
    /// Canonical short names of attributes that mark a method as a
    /// test. Covers xUnit (<c>Fact</c> / <c>Theory</c>), MSTest
    /// (<c>TestMethod</c> / <c>DataTestMethod</c>), and NUnit
    /// (<c>Test</c> / <c>TestCase</c> / <c>TestCaseSource</c>).
    /// </summary>
    public static readonly IReadOnlySet<string> TestAttributeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        // xUnit
        "Fact",
        "Theory",
        // MSTest
        "TestMethod",
        "DataTestMethod",
        // NUnit
        "Test",
        "TestCase",
        "TestCaseSource",
    };

    /// <summary>
    /// True if the supplied method declaration carries any
    /// recognised test attribute on the method itself. Inherited or
    /// class-level attributes (NUnit's <c>[TestFixture]</c>,
    /// MSTest's <c>[TestClass]</c>) are intentionally ignored —
    /// they mark the container; the resolver wants the actual test
    /// entry point.
    /// </summary>
    public static bool IsTestMethod(MethodDeclarationSyntax method)
    {
        foreach (var list in method.AttributeLists)
        {
            foreach (var attr in list.Attributes)
            {
                if (IsTestAttributeName(attr.Name))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// True when the supplied <see cref="NameSyntax"/> ends in a
    /// short name that matches <see cref="TestAttributeNames"/>.
    /// Handles `Fact`, `Xunit.Fact`, `global::Xunit.Fact`, and the
    /// `*Attribute`-suffixed variants.
    /// </summary>
    private static bool IsTestAttributeName(NameSyntax name)
    {
        var leaf = LeafIdentifier(name);
        if (string.IsNullOrEmpty(leaf)) return false;
        var stripped = leaf.EndsWith("Attribute", StringComparison.Ordinal)
            ? leaf[..^"Attribute".Length]
            : leaf;
        return TestAttributeNames.Contains(stripped);
    }

    private static string LeafIdentifier(NameSyntax name) => name switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        QualifiedNameSyntax q => LeafIdentifier(q.Right),
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        _ => string.Empty,
    };
}
