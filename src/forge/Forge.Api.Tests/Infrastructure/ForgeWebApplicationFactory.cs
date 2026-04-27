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
    private readonly Dictionary<string, string?> extraConfig;

    public ForgeWebApplicationFactory() : this(null) { }

    public ForgeWebApplicationFactory(Dictionary<string, string?>? extraConfig)
    {
        this.extraConfig = extraConfig ?? new();
    }

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Forge:Db:Path"] = this.dbPath,
                // Phase 8.10 — auto-clone shells out to `git clone`, which
                // doesn't make sense in CI / unit tests. Workstream-create
                // tests assume the auto-clone path is a no-op.
                ["Forge:Repo:AutoClone"] = "false",
                // Surface unhandled exception detail in tests so 500 responses
                // include the actual stack trace in the body.
                ["DetailedErrors"] = "true",
                ["Logging:LogLevel:Default"] = "Error",
            };
            foreach (var kv in this.extraConfig)
            {
                settings[kv.Key] = kv.Value;
            }
            config.AddInMemoryCollection(settings);
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
