// -----------------------------------------------------------------------
// <copyright file="SqliteFileSyncStateStoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests.Sync;

using Ast.Memory.Sync;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class SqliteFileSyncStateStoreTests
{
    private string tempDir = null!;
    private string dbPath = null!;

    [TestInitialize]
    public void Setup()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"sqlite_sync_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.dbPath = Path.Combine(this.tempDir, "sync_state.db");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(this.tempDir))
        {
            try { Directory.Delete(this.tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [TestMethod]
    [TestCategory("SqliteStore")]
    public async Task GetContentHashAsync_NoEntry_ReturnsNull()
    {
        var store = new SqliteFileSyncStateStore(this.dbPath);
        var hash = await store.GetContentHashAsync("nonexistent.cs");
        hash.Should().BeNull();
    }

    [TestMethod]
    [TestCategory("SqliteStore")]
    public async Task SetAndGetContentHash_RoundTrips()
    {
        var store = new SqliteFileSyncStateStore(this.dbPath);
        await store.SetContentHashAsync("src/Service.cs", "abc123hash");

        var hash = await store.GetContentHashAsync("src/Service.cs");
        hash.Should().Be("abc123hash");
    }

    [TestMethod]
    [TestCategory("SqliteStore")]
    public async Task SetContentHash_UpdatesExisting()
    {
        var store = new SqliteFileSyncStateStore(this.dbPath);
        await store.SetContentHashAsync("file.cs", "hash1");
        await store.SetContentHashAsync("file.cs", "hash2");

        var hash = await store.GetContentHashAsync("file.cs");
        hash.Should().Be("hash2");
    }

    [TestMethod]
    [TestCategory("SqliteStore")]
    public async Task RemoveAsync_DeletesEntry()
    {
        var store = new SqliteFileSyncStateStore(this.dbPath);
        await store.SetContentHashAsync("file.cs", "hash1");
        await store.RemoveAsync("file.cs");

        var hash = await store.GetContentHashAsync("file.cs");
        hash.Should().BeNull();
    }

    [TestMethod]
    [TestCategory("SqliteStore")]
    public async Task PersistsAcrossInstances()
    {
        var store1 = new SqliteFileSyncStateStore(this.dbPath);
        await store1.SetContentHashAsync("persistent.cs", "stable-hash");

        // Create a new store instance pointing at the same DB
        var store2 = new SqliteFileSyncStateStore(this.dbPath);
        var hash = await store2.GetContentHashAsync("persistent.cs");
        hash.Should().Be("stable-hash", "SQLite store should persist data across instances");
    }

    [TestMethod]
    [TestCategory("SqliteStore")]
    public async Task NormalizesPathSeparators()
    {
        var store = new SqliteFileSyncStateStore(this.dbPath);
        await store.SetContentHashAsync("src\\Service.cs", "hash-backslash");

        // Should find via forward-slash lookup since paths are normalized
        var hash = await store.GetContentHashAsync("src/Service.cs");
        hash.Should().Be("hash-backslash");
    }

    [TestMethod]
    [TestCategory("SqliteStore")]
    public void CreatesDirectoryIfNeeded()
    {
        var nestedPath = Path.Combine(this.tempDir, "nested", "dir", "sync.db");
        var store = new SqliteFileSyncStateStore(nestedPath);

        // Should not throw — directory was created
        File.Exists(nestedPath).Should().BeTrue("SQLite DB file should be created");
    }
}
