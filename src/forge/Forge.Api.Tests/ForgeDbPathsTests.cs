// -----------------------------------------------------------------------
// <copyright file="ForgeDbPathsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>
/// Phase A — unit tests for <see cref="ForgeDbPaths"/> and its slug
/// validator. Covers:
/// <list type="bullet">
///   <item>Legacy <c>Forge:Db:Path</c> precedence.</item>
///   <item>New <c>Forge:Db:Folder</c> resolution.</item>
///   <item>Default folder (<c>E:\forge</c>) when neither is set.</item>
///   <item>Folder creation on construction.</item>
///   <item>Per-product DB path derivation.</item>
///   <item>Slug validation rules (length, characters, hyphens, reserved
///         names including the control-DB <c>forge</c> collision and
///         Windows device names).</item>
/// </list>
/// </summary>
public class ForgeDbPathsTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"forge-paths-{Guid.NewGuid():N}");

    public ForgeDbPathsTests()
    {
        Directory.CreateDirectory(this.testRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.testRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    private IConfiguration Cfg(IDictionary<string, string?> entries)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
    }

    [Fact]
    public void LegacyDbPath_TakesPrecedence_OverFolder()
    {
        var explicitPath = Path.Combine(this.testRoot, "legacy", "custom.db");
        Directory.CreateDirectory(Path.Combine(this.testRoot, "legacy"));
        var paths = new ForgeDbPaths(Cfg(new Dictionary<string, string?>
        {
            ["Forge:Db:Path"] = explicitPath,
            ["Forge:Db:Folder"] = Path.Combine(this.testRoot, "should-be-ignored"),
        }));

        Assert.Equal(explicitPath, paths.ControlDbPath());
        Assert.Equal(Path.Combine(this.testRoot, "legacy"), paths.Folder);
    }

    [Fact]
    public void FolderConfig_Resolves_ControlDbAt_FolderForgeDb()
    {
        var folder = Path.Combine(this.testRoot, "folder-config");
        var paths = new ForgeDbPaths(Cfg(new Dictionary<string, string?>
        {
            ["Forge:Db:Folder"] = folder,
        }));

        Assert.Equal(folder, paths.Folder);
        Assert.Equal(Path.Combine(folder, "forge.db"), paths.ControlDbPath());
    }

    [Fact]
    public void ProductDbPath_DerivesFrom_SlugInFolder()
    {
        var folder = Path.Combine(this.testRoot, "product-paths");
        var paths = new ForgeDbPaths(Cfg(new Dictionary<string, string?>
        {
            ["Forge:Db:Folder"] = folder,
        }));

        Assert.Equal(Path.Combine(folder, "failover-cluster.db"), paths.ProductDbPath("failover-cluster"));
        Assert.Equal(Path.Combine(folder, "code-intelligence.db"), paths.ProductDbPath("code-intelligence"));
        Assert.Equal(Path.Combine(folder, "atlas.db"), paths.ProductDbPath("atlas"));
    }

    [Fact]
    public void ProductDbPath_IsCaseInsensitive_LowercasesInput()
    {
        var folder = Path.Combine(this.testRoot, "case-test");
        var paths = new ForgeDbPaths(Cfg(new Dictionary<string, string?>
        {
            ["Forge:Db:Folder"] = folder,
        }));

        Assert.Equal(Path.Combine(folder, "atlas.db"), paths.ProductDbPath("ATLAS"));
        Assert.Equal(Path.Combine(folder, "code-intelligence.db"), paths.ProductDbPath("Code-Intelligence"));
    }

    [Fact]
    public void Folder_IsCreated_OnConstruction()
    {
        var folder = Path.Combine(this.testRoot, "fresh-folder");
        Assert.False(Directory.Exists(folder));

        _ = new ForgeDbPaths(Cfg(new Dictionary<string, string?>
        {
            ["Forge:Db:Folder"] = folder,
        }));

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void ProductDbExists_ReturnsTrue_WhenFilePresent()
    {
        var folder = Path.Combine(this.testRoot, "exists-test");
        var paths = new ForgeDbPaths(Cfg(new Dictionary<string, string?>
        {
            ["Forge:Db:Folder"] = folder,
        }));

        Assert.False(paths.ProductDbExists("atlas"));
        File.WriteAllText(paths.ProductDbPath("atlas"), string.Empty);
        Assert.True(paths.ProductDbExists("atlas"));
    }

    // ── TryValidateSlug ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("atlas")]
    [InlineData("code-intelligence")]
    [InlineData("failover-cluster")]
    [InlineData("a1")]
    [InlineData("project-2026")]
    public void TryValidateSlug_AcceptsValidSlugs(string slug)
    {
        Assert.True(ForgeDbPaths.TryValidateSlug(slug, out var error), error);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidateSlug_RejectsEmpty(string? slug)
    {
        Assert.False(ForgeDbPaths.TryValidateSlug(slug, out var error));
        Assert.NotNull(error);
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("hello world")]      // space
    [InlineData("foo_bar")]          // underscore
    [InlineData("foo.bar")]          // dot
    [InlineData("foo/bar")]          // slash (path traversal!)
    [InlineData("..\\evil")]         // path traversal!
    [InlineData("foo@bar")]          // at-sign
    [InlineData("café")]             // non-ASCII
    public void TryValidateSlug_RejectsInvalidCharacters(string slug)
    {
        Assert.False(ForgeDbPaths.TryValidateSlug(slug, out var error));
        Assert.NotNull(error);
        Assert.Contains("invalid", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("-foo")]
    [InlineData("foo-")]
    [InlineData("-")]
    public void TryValidateSlug_RejectsLeadingOrTrailingHyphen(string slug)
    {
        Assert.False(ForgeDbPaths.TryValidateSlug(slug, out var error));
        Assert.NotNull(error);
        Assert.Contains("hyphen", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("foo--bar")]
    [InlineData("a---b")]
    public void TryValidateSlug_RejectsConsecutiveHyphens(string slug)
    {
        Assert.False(ForgeDbPaths.TryValidateSlug(slug, out var error));
        Assert.NotNull(error);
        Assert.Contains("consecutive", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateSlug_RejectsOverLengthSlug()
    {
        var slug = new string('a', 101);
        Assert.False(ForgeDbPaths.TryValidateSlug(slug, out var error));
        Assert.NotNull(error);
        Assert.Contains("100", error);
    }

    [Theory]
    [InlineData("forge")]
    [InlineData("FORGE")] // case-insensitive
    [InlineData("con")]
    [InlineData("nul")]
    [InlineData("aux")]
    [InlineData("prn")]
    [InlineData("com1")]
    [InlineData("com9")]
    [InlineData("lpt1")]
    [InlineData("lpt9")]
    public void TryValidateSlug_RejectsReservedNames(string slug)
    {
        Assert.False(ForgeDbPaths.TryValidateSlug(slug, out var error));
        Assert.NotNull(error);
        Assert.Contains("reserved", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductDbPath_ThrowsOnInvalidSlug()
    {
        var folder = Path.Combine(this.testRoot, "invalid-slug");
        var paths = new ForgeDbPaths(Cfg(new Dictionary<string, string?>
        {
            ["Forge:Db:Folder"] = folder,
        }));

        Assert.Throws<ArgumentException>(() => paths.ProductDbPath("../evil"));
        Assert.Throws<ArgumentException>(() => paths.ProductDbPath(""));
        Assert.Throws<ArgumentException>(() => paths.ProductDbPath("forge"));
    }

    [Fact]
    public void DefaultFolder_IsUsedWhen_NoConfigSet()
    {
        // Building with an empty config — but ForgeDbPaths tries to
        // CreateDirectory on E:\forge in its ctor. Skip if running on a
        // host where E:\ doesn't exist; otherwise the default folder
        // bootstrap is exercised by ForgeWebApplicationFactory in every
        // integration test.
        if (!Directory.Exists(@"E:\") || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return;
        }

        var paths = new ForgeDbPaths(Cfg(new Dictionary<string, string?>()));
        Assert.Equal(@"E:\forge", paths.Folder);
        Assert.Equal(@"E:\forge\forge.db", paths.ControlDbPath());
    }
}
