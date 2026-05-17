// -----------------------------------------------------------------------
// <copyright file="ProductRuntimeWatermarkTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase G (e-due-index) — smoke tests for the control-side
/// <c>product_runtime</c> watermark used by background workers to skip
/// products with no pending work without opening per-product DBs.
/// </summary>
public class ProductRuntimeWatermarkTests
{
    [Fact]
    public async Task StampPending_UpsertsLastPendingAt()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot DI */ }

        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        control.Products.Add(new ProductRow
        {
            Id = "p-watermark", Slug = "watermark", Name = "watermark",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await control.SaveChangesAsync();

        await ProductRuntimeWatermark.StampPendingAsync(control, "p-watermark");

        var row = await control.ProductRuntime.AsNoTracking().FirstAsync(r => r.ProductId == "p-watermark");
        Assert.NotNull(row.LastPendingAt);
        Assert.Equal(0, row.ActivePrCount);

        // Re-stamp should update timestamp without inserting a duplicate row.
        var first = row.LastPendingAt!.Value;
        await Task.Delay(20);
        await ProductRuntimeWatermark.StampPendingAsync(control, "p-watermark");
        var row2 = await control.ProductRuntime.AsNoTracking().FirstAsync(r => r.ProductId == "p-watermark");
        Assert.True(row2.LastPendingAt > first);
        Assert.Equal(1, await control.ProductRuntime.AsNoTracking().CountAsync(r => r.ProductId == "p-watermark"));
    }

    [Fact]
    public async Task SetActivePrCount_UpsertsCount_PreservingPendingAt()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot DI */ }

        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        control.Products.Add(new ProductRow
        {
            Id = "p-prcount", Slug = "prcount", Name = "prcount",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await control.SaveChangesAsync();

        await ProductRuntimeWatermark.StampPendingAsync(control, "p-prcount");
        await ProductRuntimeWatermark.SetActivePrCountAsync(control, "p-prcount", 3);

        var row = await control.ProductRuntime.AsNoTracking().FirstAsync(r => r.ProductId == "p-prcount");
        Assert.NotNull(row.LastPendingAt);
        Assert.Equal(3, row.ActivePrCount);

        await ProductRuntimeWatermark.SetActivePrCountAsync(control, "p-prcount", 0);
        var row2 = await control.ProductRuntime.AsNoTracking().FirstAsync(r => r.ProductId == "p-prcount");
        Assert.Equal(0, row2.ActivePrCount);
        // LastPendingAt is preserved (we only updated the PR count).
        Assert.Equal(row.LastPendingAt, row2.LastPendingAt);
    }

    [Fact]
    public async Task ClearPending_ResetsLastPendingAt_KeepingRow()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot DI */ }

        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        control.Products.Add(new ProductRow
        {
            Id = "p-clear", Slug = "clear", Name = "clear",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await control.SaveChangesAsync();

        await ProductRuntimeWatermark.StampPendingAsync(control, "p-clear");
        await ProductRuntimeWatermark.SetActivePrCountAsync(control, "p-clear", 7);

        await ProductRuntimeWatermark.ClearPendingAsync(control, "p-clear");
        var row = await control.ProductRuntime.AsNoTracking().FirstAsync(r => r.ProductId == "p-clear");
        Assert.Null(row.LastPendingAt);
        // ActivePrCount is preserved through ClearPendingAsync.
        Assert.Equal(7, row.ActivePrCount);
    }

    [Fact]
    public async Task ListDueProducts_ReturnsOnlyProductsWithPendingOrOpenPrs()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot DI */ }

        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        control.Products.Add(new ProductRow { Id = "p-due-a", Slug = "due-a", Name = "due-a", CreatedAt = DateTimeOffset.UtcNow });
        control.Products.Add(new ProductRow { Id = "p-due-b", Slug = "due-b", Name = "due-b", CreatedAt = DateTimeOffset.UtcNow });
        control.Products.Add(new ProductRow { Id = "p-due-c", Slug = "due-c", Name = "due-c", CreatedAt = DateTimeOffset.UtcNow });
        await control.SaveChangesAsync();

        // a → pending stamped
        await ProductRuntimeWatermark.StampPendingAsync(control, "p-due-a");
        // b → no pending but 2 open PRs
        await ProductRuntimeWatermark.SetActivePrCountAsync(control, "p-due-b", 2);
        // c → nothing — should NOT be due

        var due = await ProductRuntimeWatermark.ListDueProductsAsync(control);
        Assert.Contains("due-a", due);
        Assert.Contains("due-b", due);
        Assert.DoesNotContain("due-c", due);
    }

    [Fact]
    public async Task PhaseGDueIndexMigration_IsIdempotent()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot — runs migration */ }

        // Migration is already applied on boot. Re-run should be a no-op.
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        await PhaseGDueIndexMigration.ApplyAsync(control);
        await PhaseGDueIndexMigration.ApplyAsync(control);

        // Table + index should be present. The EF model maps product_runtime
        // so an empty SELECT verifies the schema landed.
        var count = await control.ProductRuntime.AsNoTracking().CountAsync();
        Assert.True(count >= 0);
    }
}
