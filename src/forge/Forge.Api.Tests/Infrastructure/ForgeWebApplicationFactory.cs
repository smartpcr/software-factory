// -----------------------------------------------------------------------
// <copyright file="ForgeWebApplicationFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Infrastructure;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Test factory that points the SQLite DB at a per-instance temp path so tests are isolated.
/// </summary>
public class ForgeWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string dbPath = Path.Combine(Path.GetTempPath(), $"forge-test-{Guid.NewGuid():N}.db");

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forge:Db:Path"] = this.dbPath,
            });
        });
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(this.dbPath))
        {
            try { File.Delete(this.dbPath); }
            catch (IOException) { /* file lock — best effort */ }
        }
    }
}
