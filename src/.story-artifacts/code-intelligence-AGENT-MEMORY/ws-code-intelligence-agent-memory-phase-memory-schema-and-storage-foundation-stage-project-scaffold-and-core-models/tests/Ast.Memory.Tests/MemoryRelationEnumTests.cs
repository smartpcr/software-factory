using Ast.Core;
using Xunit;

namespace Ast.Memory.Tests;

/// <summary>
/// Validates that adding HyperedgeType.MemoryRelation is additive-only
/// and does not break existing enum values.
/// </summary>
public class MemoryRelationEnumTests
{
    [Fact]
    public void MemoryRelation_ExistsInEnum()
    {
        var value = HyperedgeType.MemoryRelation;
        Assert.Equal(6, (int)value);
    }

    [Fact]
    public void ExistingValues_AreUnchanged()
    {
        Assert.Equal(0, (int)HyperedgeType.Contains);
        Assert.Equal(1, (int)HyperedgeType.Calls);
        Assert.Equal(2, (int)HyperedgeType.Imports);
        Assert.Equal(3, (int)HyperedgeType.Inherits);
        Assert.Equal(4, (int)HyperedgeType.Expertise);
        Assert.Equal(5, (int)HyperedgeType.SimilarTo);
    }

    [Fact]
    public void MemoryRelation_IsAdditiveOnly()
    {
        // MemoryRelation must have the highest value — no existing values shifted
        var allValues = Enum.GetValues<HyperedgeType>();
        var maxExistingBeforeMemoryRelation = 5; // SimilarTo

        Assert.True((int)HyperedgeType.MemoryRelation > maxExistingBeforeMemoryRelation);
    }

    [Fact]
    public void MemoryRelation_CanBeUsedInHyperedge()
    {
        var edge = new Hyperedge("e1", HyperedgeType.MemoryRelation, ["s1"], ["t1"],
            new Dictionary<string, string>
            {
                ["SemanticRelation"] = "APPLIES_TO"
            });

        Assert.Equal(HyperedgeType.MemoryRelation, edge.EdgeType);
    }
}
