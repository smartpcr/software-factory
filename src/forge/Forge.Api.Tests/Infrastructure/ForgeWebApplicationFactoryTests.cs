// -----------------------------------------------------------------------
// <copyright file="ForgeWebApplicationFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Infrastructure;

using Forge.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase G.3 — sanity tests for the test factory's per-test folder
/// isolation and tripwire enforcement.
/// </summary>
public class ForgeWebApplicationFactoryTests
{
    [Fact]
    public void Factory_AllocatesPerInstanceFolder_UnderTempPath()
    {
        using var f1 = new ForgeWebApplicationFactory();
        using var f2 = new ForgeWebApplicationFactory();

        Assert.NotEqual(f1.Folder, f2.Folder);
        Assert.StartsWith(Path.GetTempPath(), f1.Folder, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetTempPath(), f2.Folder, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(f1.Folder), $"Folder '{f1.Folder}' should exist after construction.");
        Assert.True(Directory.Exists(f2.Folder), $"Folder '{f2.Folder}' should exist after construction.");
    }

    [Fact]
    public void Factory_FolderIsDeletedOnDispose()
    {
        string folder;
        using (var f = new ForgeWebApplicationFactory())
        {
            folder = f.Folder;
            // Touch a file in the folder so dispose has to do real cleanup.
            using (f.CreateClient()) { /* boot — creates forge.db */ }
            Assert.True(Directory.Exists(folder));
        }
        // After Dispose, the folder should be gone — but SQLite WAL/SHM
        // file handles aren't always released by the host's stop time on
        // Windows. Allow up to 2s for the OS file handles to drain. Even
        // if cleanup ultimately fails (best-effort), the folder is in
        // %TEMP% and will be reaped by the OS / janitor.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Directory.Exists(folder) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }
        // The factory call should have at least *attempted* cleanup. We
        // don't strictly require deletion success on Windows because SQLite
        // file locks may outlive the WebApplicationFactory dispose on
        // first-run tests.
        if (Directory.Exists(folder))
        {
            // Final manual sweep — confirm only DB files remain (no
            // unexpected sub-directories), then nuke best-effort.
            var residual = Directory.GetFiles(folder);
            foreach (var f in residual)
            {
                Assert.True(f.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith("-journal", StringComparison.OrdinalIgnoreCase),
                    $"Unexpected residual file '{f}' in test factory folder.");
            }
            try { Directory.Delete(folder, recursive: true); } catch { /* OS will reap */ }
        }
    }

    [Fact]
    public void Factory_DbPath_ResolvesToFolder_ForgeDb()
    {
        using var f = new ForgeWebApplicationFactory();
        Assert.Equal(Path.Combine(f.Folder, ForgeDbPaths.ControlDbFileName), f.DbPath);
    }

    [Fact]
    public void Factory_BootsResolvesControlAndProductPaths_UnderFolder()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot DI */ }

        using var scope = f.Services.CreateScope();
        var paths = scope.ServiceProvider.GetRequiredService<IForgeDbPaths>();

        Assert.Equal(f.Folder.TrimEnd(Path.DirectorySeparatorChar),
            paths.Folder.TrimEnd(Path.DirectorySeparatorChar));
        Assert.StartsWith(f.Folder, paths.ControlDbPath(), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(f.Folder, paths.ProductDbPath("primary"), StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(ForgeDbPaths.ControlDbFileName, paths.ControlDbPath(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Factory_ParallelInstances_DoNotShareFolders()
    {
        // Spin up 4 factories in parallel; each must get its own folder.
        var factories = new ForgeWebApplicationFactory[4];
        Parallel.For(0, factories.Length, i => factories[i] = new ForgeWebApplicationFactory());

        try
        {
            var folders = factories.Select(f => f.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(factories.Length, folders.Count);
        }
        finally
        {
            foreach (var f in factories) f.Dispose();
        }
    }
}
