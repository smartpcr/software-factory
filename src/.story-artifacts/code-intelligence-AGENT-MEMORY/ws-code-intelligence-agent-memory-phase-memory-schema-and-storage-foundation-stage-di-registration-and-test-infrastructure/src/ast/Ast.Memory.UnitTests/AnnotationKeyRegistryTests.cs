// -----------------------------------------------------------------------
// <copyright file="AnnotationKeyRegistryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.Memory.Models;
using Ast.Memory.Schema;

[TestClass]
public class AnnotationKeyRegistryTests
{
    [TestMethod]
    [TestCategory("Schema")]
    public void Constructor_PopulatesBuiltInKeysFromMemoryMetadataKeys()
    {
        // Arrange & Act
        var registry = new AnnotationKeyRegistry();

        // Assert — spot-check a few well-known keys
        registry.IsRegistered(MemoryMetadataKeys.MemoryLayer).Should().BeTrue();
        registry.IsRegistered(MemoryMetadataKeys.ContentHash).Should().BeTrue();
        registry.IsRegistered(MemoryMetadataKeys.SemanticRelation).Should().BeTrue();
        registry.IsRegistered(MemoryMetadataKeys.SinceCommit).Should().BeTrue();
        registry.IsRegistered(MemoryMetadataKeys.Confidence).Should().BeTrue();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Constructor_RegistersBuiltInKeysWithConcreteTypes()
    {
        // Arrange & Act
        var registry = new AnnotationKeyRegistry();

        // Assert — built-in keys must have specific typed schemas, not typeof(object)
        registry.GetDescriptor(MemoryMetadataKeys.MemoryLayer)!.ValueType.Should().Be(typeof(string));
        registry.GetDescriptor(MemoryMetadataKeys.ContentHash)!.ValueType.Should().Be(typeof(string));
        registry.GetDescriptor(MemoryMetadataKeys.Confidence)!.ValueType.Should().Be(typeof(double));
        registry.GetDescriptor(MemoryMetadataKeys.MemberCount)!.ValueType.Should().Be(typeof(int));
        registry.GetDescriptor(MemoryMetadataKeys.SimilarityScore)!.ValueType.Should().Be(typeof(double));
        registry.GetDescriptor(MemoryMetadataKeys.Stale)!.ValueType.Should().Be(typeof(bool));
        registry.GetDescriptor(MemoryMetadataKeys.LastTouched)!.ValueType.Should().Be(typeof(DateTimeOffset));
        registry.GetDescriptor(MemoryMetadataKeys.Embedding)!.ValueType.Should().Be(typeof(float[]));
        registry.GetDescriptor(MemoryMetadataKeys.EmbeddingModel)!.ValueType.Should().Be(typeof(string));
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Constructor_NoBuiltInKeyUsesTypeOfObject()
    {
        // Arrange & Act
        var registry = new AnnotationKeyRegistry();
        var keys = registry.GetRegisteredKeys();

        // Assert — every built-in must have a concrete type, never typeof(object)
        foreach (var descriptor in keys.Where(k => k.BuiltIn))
        {
            descriptor.ValueType.Should().NotBe(typeof(object),
                because: $"built-in key '{descriptor.Key}' must have a concrete typed schema");
        }
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Validate_ExtPrefixedKey_NoWarning()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();
        var metadata = new Dictionary<string, object?>
        {
            ["ext:team-owner"] = "platform-team"
        };

        // Act
        var result = registry.Validate(metadata);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Validate_RegisteredKey_CorrectType_NoWarning()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();
        var metadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.MemoryLayer] = "structural",
            [MemoryMetadataKeys.ContentHash] = "abc123",
            [MemoryMetadataKeys.Confidence] = 0.95,
            [MemoryMetadataKeys.MemberCount] = 42,
            [MemoryMetadataKeys.Stale] = false
        };

        // Act
        var result = registry.Validate(metadata);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Validate_RegisteredKey_WrongType_ProducesWarning()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();
        var metadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.Confidence] = "not-a-double"
        };

        // Act
        var result = registry.Validate(metadata);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Warnings.Should().ContainSingle()
            .Which.Should().Contain("Confidence")
            .And.Contain("Double")
            .And.Contain("String");
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Validate_RegisteredKey_WrongNumericType_ProducesWarning()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();
        var metadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.MemberCount] = 3.14  // double instead of int
        };

        // Act
        var result = registry.Validate(metadata);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Warnings.Should().ContainSingle()
            .Which.Should().Contain("MemberCount");
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Validate_RegisteredKey_NullValue_NoWarning()
    {
        // Arrange — null is acceptable for any registered key
        var registry = new AnnotationKeyRegistry();
        var metadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.Confidence] = null,
            [MemoryMetadataKeys.MemoryLayer] = null
        };

        // Act
        var result = registry.Validate(metadata);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Validate_UnknownKeyWithoutExtPrefix_ProducesWarning()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();
        var metadata = new Dictionary<string, object?>
        {
            ["unknown-key"] = "some-value"
        };

        // Act
        var result = registry.Validate(metadata);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Warnings.Should().ContainSingle()
            .Which.Should().Contain("unknown-key");
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Validate_MixedKeys_WarnsOnlyInvalid()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();
        var metadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.MemoryLayer] = "structural",
            ["ext:custom-metric"] = 42,
            ["unregistered-a"] = "x",
            ["unregistered-b"] = "y"
        };

        // Act
        var result = registry.Validate(metadata);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Warnings.Should().HaveCount(2);
        result.Warnings.Should().Contain(w => w.Contains("unregistered-a"));
        result.Warnings.Should().Contain(w => w.Contains("unregistered-b"));
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Validate_MixedTypeMismatchAndUnknown_ProducesMultipleWarnings()
    {
        // Arrange — both an unknown key and a type mismatch
        var registry = new AnnotationKeyRegistry();
        var metadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.Confidence] = "wrong-type",
            ["bogus-key"] = "whatever"
        };

        // Act
        var result = registry.Validate(metadata);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Warnings.Should().HaveCount(2);
        result.Warnings.Should().Contain(w => w.Contains("Confidence") && w.Contains("Double"));
        result.Warnings.Should().Contain(w => w.Contains("bogus-key"));
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Register_CustomKey_CorrectType_ValidatesWithoutWarning()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();
        registry.Register("custom-team-id", typeof(string));

        var metadata = new Dictionary<string, object?>
        {
            ["custom-team-id"] = "team-alpha"
        };

        // Act
        var result = registry.Validate(metadata);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Register_CustomKey_WrongType_ProducesWarning()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();
        registry.Register("custom-count", typeof(int));

        var metadata = new Dictionary<string, object?>
        {
            ["custom-count"] = "not-an-int"
        };

        // Act
        var result = registry.Validate(metadata);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Warnings.Should().ContainSingle()
            .Which.Should().Contain("custom-count");
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Register_NullOrEmptyKey_ThrowsArgumentException()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();

        // Act & Assert
        var act1 = () => registry.Register(null!);
        var act2 = () => registry.Register(string.Empty);

        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Validate_EmptyMetadata_NoWarning()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();
        var metadata = new Dictionary<string, object?>();

        // Act
        var result = registry.Validate(metadata);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Validate_NullMetadata_ThrowsArgumentNullException()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();

        // Act
        var act = () => registry.Validate(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void GetRegisteredKeys_ReturnsAllBuiltInKeys()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();

        // Act
        var keys = registry.GetRegisteredKeys();

        // Assert — all 19 MemoryMetadataKeys constants must be registered
        keys.Where(k => k.BuiltIn).Should().HaveCount(19);
        keys.Should().Contain(k => k.Key == MemoryMetadataKeys.MemoryLayer && k.BuiltIn);
        keys.Should().Contain(k => k.Key == MemoryMetadataKeys.Embedding && k.BuiltIn);
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void GetRegisteredKeys_IncludesCustomRegistrations()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();
        registry.Register("my-custom-key", typeof(int));

        // Act
        var keys = registry.GetRegisteredKeys();

        // Assert
        keys.Should().Contain(k => k.Key == "my-custom-key" && !k.BuiltIn && k.ValueType == typeof(int));
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void GetDescriptor_RegisteredKey_ReturnsDescriptor()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();

        // Act
        var descriptor = registry.GetDescriptor(MemoryMetadataKeys.Confidence);

        // Assert
        descriptor.Should().NotBeNull();
        descriptor!.Key.Should().Be(MemoryMetadataKeys.Confidence);
        descriptor.ValueType.Should().Be(typeof(double));
        descriptor.BuiltIn.Should().BeTrue();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void GetDescriptor_UnregisteredKey_ReturnsNull()
    {
        // Arrange
        var registry = new AnnotationKeyRegistry();

        // Act
        var descriptor = registry.GetDescriptor("non-existent-key");

        // Assert
        descriptor.Should().BeNull();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void TemporalKeys_SinceIndex_RegisteredAsInt()
    {
        // Arrange & Act
        var registry = new AnnotationKeyRegistry();
        var descriptor = registry.GetDescriptor(MemoryMetadataKeys.SinceIndex);

        // Assert — SinceIndex maps to int per VersionRange(string? Since, int? SinceIndex, ...)
        descriptor.Should().NotBeNull();
        descriptor!.ValueType.Should().Be(typeof(int));
        descriptor.BuiltIn.Should().BeTrue();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void TemporalKeys_UntilIndex_RegisteredAsInt()
    {
        // Arrange & Act
        var registry = new AnnotationKeyRegistry();
        var descriptor = registry.GetDescriptor(MemoryMetadataKeys.UntilIndex);

        // Assert — UntilIndex maps to int per VersionRange(..., int? UntilIndex)
        descriptor.Should().NotBeNull();
        descriptor!.ValueType.Should().Be(typeof(int));
        descriptor.BuiltIn.Should().BeTrue();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void TemporalKeys_SinceCommit_RegisteredAsString()
    {
        // Arrange & Act
        var registry = new AnnotationKeyRegistry();
        var descriptor = registry.GetDescriptor(MemoryMetadataKeys.SinceCommit);

        // Assert — SinceCommit maps to string (commit SHA)
        descriptor.Should().NotBeNull();
        descriptor!.ValueType.Should().Be(typeof(string));
        descriptor.BuiltIn.Should().BeTrue();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void TemporalKeys_UntilCommit_RegisteredAsString()
    {
        // Arrange & Act
        var registry = new AnnotationKeyRegistry();
        var descriptor = registry.GetDescriptor(MemoryMetadataKeys.UntilCommit);

        // Assert — UntilCommit maps to string (commit SHA)
        descriptor.Should().NotBeNull();
        descriptor!.ValueType.Should().Be(typeof(string));
        descriptor.BuiltIn.Should().BeTrue();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Validate_TemporalMetadata_CorrectTypes_NoWarning()
    {
        // Arrange — all temporal keys with their correct types
        var registry = new AnnotationKeyRegistry();
        var metadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.SinceCommit] = "abc123def",
            [MemoryMetadataKeys.SinceIndex] = 5,
            [MemoryMetadataKeys.UntilCommit] = "def456abc",
            [MemoryMetadataKeys.UntilIndex] = 10
        };

        // Act
        var result = registry.Validate(metadata);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
    }

    [TestMethod]
    [TestCategory("Schema")]
    public void Validate_TemporalMetadata_StringForIndex_ProducesWarning()
    {
        // Arrange — SinceIndex/UntilIndex expect int, not string
        var registry = new AnnotationKeyRegistry();
        var metadata = new Dictionary<string, object?>
        {
            [MemoryMetadataKeys.SinceIndex] = "not-an-int",
            [MemoryMetadataKeys.UntilIndex] = "also-not-int"
        };

        // Act
        var result = registry.Validate(metadata);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Warnings.Should().HaveCount(2);
        result.Warnings.Should().Contain(w => w.Contains("SinceIndex") && w.Contains("Int32"));
        result.Warnings.Should().Contain(w => w.Contains("UntilIndex") && w.Contains("Int32"));
    }
}