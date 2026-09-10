// -----------------------------------------------------------------------
// <copyright file="Phase101Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

public static class Phase101Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        _ = control;
        using var schema = new Schema(new DbContextOptionsBuilder<Schema>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.ExecuteSqlRawAsync(Phase100Migration.Idempotent(schema.Database.GenerateCreateScript()), ct);
    }

    private sealed class Schema(DbContextOptions<Schema> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder mb) => ChatDeliveryModel.ConfigureProduct(mb);
    }
}