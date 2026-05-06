// -----------------------------------------------------------------------
// <copyright file="ExtractionModelsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using System.Linq;
using Ast.Memory.Models;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class ExtractionModelsTests
{
    [TestMethod]
    public void ExtractionOptions_DefaultLanguages_AreCSharpAndPowerShell()
    {
        var options = new ExtractionOptions();

        options.Languages.Should().BeEquivalentTo(new[] { "C#", "PowerShell" });
    }

    [TestMethod]
    public void ExtractionOptions_DefaultIncludeTestProjects_IsFalse()
    {
        var options = new ExtractionOptions();

        options.IncludeTestProjects.Should().BeFalse();
    }

    [TestMethod]
    public void ExtractionOptions_DefaultGenerateEmbeddings_IsTrue()
    {
        var options = new ExtractionOptions();

        options.GenerateEmbeddings.Should().BeTrue();
    }

    [TestMethod]
    public void ExtractionOptions_CommitSha_DefaultsToNull()
    {
        var options = new ExtractionOptions();

        options.CommitSha.Should().BeNull();
    }

    [TestMethod]
    public void ExtractionResult_Empty_HasZeroCounts()
    {
        var empty = ExtractionResult.Empty;

        empty.NodesCreated.Should().Be(0);
        empty.EdgesCreated.Should().Be(0);
        empty.EmbeddingsGenerated.Should().Be(0);
        empty.Errors.Should().BeEmpty();
    }

    [TestMethod]
    public void ExtractionResult_Merge_CombinesCounts()
    {
        var a = new ExtractionResult
        {
            NodesCreated = 10,
            EdgesCreated = 5,
            EmbeddingsGenerated = 3,
            Errors = new[] { "Error A" },
        };
        var b = new ExtractionResult
        {
            NodesCreated = 20,
            EdgesCreated = 15,
            EmbeddingsGenerated = 7,
            Errors = new[] { "Error B" },
        };

        var merged = a.Merge(b);

        merged.NodesCreated.Should().Be(30);
        merged.EdgesCreated.Should().Be(20);
        merged.EmbeddingsGenerated.Should().Be(10);
        merged.Errors.Should().HaveCount(2);
    }

    [TestMethod]
    public void StageResult_Empty_HasZeroCounts()
    {
        var empty = StageResult.Empty;

        empty.NodesCreated.Should().Be(0);
        empty.EdgesCreated.Should().Be(0);
        empty.EmbeddingsGenerated.Should().Be(0);
        empty.Errors.Should().BeEmpty();
    }

    [TestMethod]
    public void StageResult_ToExtractionResult_CopiesValues()
    {
        var stage = new StageResult
        {
            NodesCreated = 5,
            EdgesCreated = 3,
            EmbeddingsGenerated = 2,
            Errors = new[] { "warn" },
        };

        var extraction = stage.ToExtractionResult();

        extraction.NodesCreated.Should().Be(5);
        extraction.EdgesCreated.Should().Be(3);
        extraction.EmbeddingsGenerated.Should().Be(2);
        extraction.Errors.Should().ContainSingle("warn");
    }

    [TestMethod]
    public void NormalizationResult_Defaults_AreZero()
    {
        var result = new NormalizationResult();

        result.NodesNormalized.Should().Be(0);
        result.DuplicatesMerged.Should().Be(0);
        result.IdMappings.Should().BeEmpty();
        result.MergedStubFiles.Should().BeEmpty();
    }
}
