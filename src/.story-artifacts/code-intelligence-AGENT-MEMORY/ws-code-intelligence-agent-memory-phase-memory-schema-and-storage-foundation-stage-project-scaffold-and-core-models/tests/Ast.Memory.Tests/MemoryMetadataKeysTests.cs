// -----------------------------------------------------------------------
// <copyright file="MemoryMetadataKeysTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using Ast.Memory.Models;

[TestClass]
public class MemoryMetadataKeysTests
{
    [TestMethod]
    public void MemoryMetadataKeys_AllConstants_AreNotNullOrEmpty()
    {
        var fields = typeof(MemoryMetadataKeys)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToList();

        fields.Should().HaveCountGreaterOrEqualTo(18, "architecture.md §4.5 defines 18 keys");

        foreach (var field in fields)
        {
            var value = (string?)field.GetValue(null);
            value.Should().NotBeNullOrWhiteSpace($"Key constant '{field.Name}' must have a value");
        }
    }

    [TestMethod]
    public void MemoryMetadataKeys_ContainsAllRequiredKeys()
    {
        // Verify all required keys exist as constants
        MemoryMetadataKeys.MemoryLayer.Should().Be("MemoryLayer");
        MemoryMetadataKeys.SemanticRelation.Should().Be("SemanticRelation");
        MemoryMetadataKeys.ContentHash.Should().Be("ContentHash");
        MemoryMetadataKeys.SinceCommit.Should().Be("SinceCommit");
        MemoryMetadataKeys.SinceIndex.Should().Be("SinceIndex");
        MemoryMetadataKeys.UntilCommit.Should().Be("UntilCommit");
        MemoryMetadataKeys.UntilIndex.Should().Be("UntilIndex");
        MemoryMetadataKeys.Summary.Should().Be("Summary");
        MemoryMetadataKeys.MemberCount.Should().Be("MemberCount");
        MemoryMetadataKeys.SimilarityScore.Should().Be("SimilarityScore");
        MemoryMetadataKeys.OriginalEdgeType.Should().Be("OriginalEdgeType");
        MemoryMetadataKeys.Confidence.Should().Be("Confidence");
        MemoryMetadataKeys.Stale.Should().Be("Stale");
        MemoryMetadataKeys.QualifiedName.Should().Be("QualifiedName");
        MemoryMetadataKeys.LastTouched.Should().Be("LastTouched");
        MemoryMetadataKeys.Embedding.Should().Be("Embedding");
        MemoryMetadataKeys.EmbeddingModel.Should().Be("EmbeddingModel");
        MemoryMetadataKeys.Package.Should().Be("Package");
    }
}
