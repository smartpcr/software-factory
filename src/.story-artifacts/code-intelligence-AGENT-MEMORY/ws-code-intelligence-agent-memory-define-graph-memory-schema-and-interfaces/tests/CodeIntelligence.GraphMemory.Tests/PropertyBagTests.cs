using CodeIntelligence.GraphMemory.Schema;
using Xunit;

namespace CodeIntelligence.GraphMemory.Tests;

public class PropertyBagTests
{
    [Fact]
    public void SetAndGet_StringValue()
    {
        var bag = new PropertyBag();
        bag.Set("key", "hello");

        Assert.True(bag.TryGet<string>("key", out var value));
        Assert.Equal("hello", value);
    }

    [Fact]
    public void SetAndGet_NumericValue()
    {
        var bag = new PropertyBag();
        bag.Set("count", 42);

        Assert.True(bag.TryGet<int>("count", out var value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void SetAndGet_BoolValue()
    {
        var bag = new PropertyBag();
        bag.Set("flag", true);

        Assert.True(bag.TryGet<bool>("flag", out var value));
        Assert.True(value);
    }

    [Fact]
    public void SetAndGet_ArrayValue()
    {
        var bag = new PropertyBag();
        bag.Set("tags", new[] { "a", "b", "c" });

        Assert.True(bag.TryGet<string[]>("tags", out var value));
        Assert.Equal(["a", "b", "c"], value);
    }

    [Fact]
    public void SetAndGet_NestedObject()
    {
        var bag = new PropertyBag();
        bag.Set("nested", new { X = 1, Y = "two" });

        Assert.True(bag.TryGet<Dictionary<string, object>>("nested", out _));
    }

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        var bag = new PropertyBag();
        Assert.False(bag.TryGet<string>("nope", out _));
    }

    [Fact]
    public void Remove_RemovesKey()
    {
        var bag = new PropertyBag();
        bag.Set("x", 1);
        Assert.True(bag.Remove("x"));
        Assert.Empty(bag);
    }

    [Fact]
    public void Count_ReflectsEntries()
    {
        var bag = new PropertyBag();
        Assert.Empty(bag);

        bag.Set("a", 1);
        bag.Set("b", 2);
        Assert.Equal(2, bag.Count);
    }

    [Theory]
    [InlineData("1startsWithDigit")]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("-leadingDash")]
    public void InvalidKey_ThrowsArgumentException(string key)
    {
        var bag = new PropertyBag();
        Assert.Throws<ArgumentException>(() => bag.Set(key, "value"));
    }

    [Theory]
    [InlineData("validKey")]
    [InlineData("neo4j.label")]
    [InlineData("a123")]
    [InlineData("cosine_similarity")]
    public void ValidKey_DoesNotThrow(string key)
    {
        var bag = new PropertyBag();
        bag.Set(key, "value");
        Assert.True(bag.ContainsKey(key));
    }
}
