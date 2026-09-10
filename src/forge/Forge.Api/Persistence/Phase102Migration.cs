// -----------------------------------------------------------------------
// <copyright file="Phase102Migration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

public static class Phase102Migration
{
    public static async Task ApplyAsync(ControlDbContext control, ProductDbContext db, CancellationToken ct = default)
    {
        _ = db;
        using var schema = new Schema(new DbContextOptionsBuilder<Schema>().UseSqlite("Data Source=:memory:").Options);
        await control.Database.ExecuteSqlRawAsync(Phase100Migration.Idempotent(schema.Database.GenerateCreateScript()), ct);
    }
    private sealed class Schema(DbContextOptions<Schema> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder mb) => ChatDeliveryModel.ConfigureReception(mb);
    }
}