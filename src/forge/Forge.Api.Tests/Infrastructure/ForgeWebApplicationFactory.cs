// -----------------------------------------------------------------------
// <copyright file="ForgeWebApplicationFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Infrastructure;

using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Test factory that points the SQLite DB at a per-instance temp path so
/// tests are isolated.
///
/// <para>
/// Hardening (after a leak that polluted the production DB at
/// <c>E:\forge\forge.db</c> with 51 test spawns): the factory installs the
/// override via THREE mechanisms — the <c>FORGE_DB_PATH</c> env var (which
/// `WebApplicationBuilder` picks up before any `appsettings.json` reads),
/// the in-memory <c>IConfiguration</c> source on the test web host, AND a
/// post-build sanity check that asserts the resolved path is under
/// <c>%TEMP%</c>. Any of those failing is a test setup bug we want to
/// catch loud, not a silent prod-DB write.
/// </para>
/// </summary>
public class ForgeWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string ProdDbDefault = @"E:\forge\forge.db";
    private const string EnvVarName = "FORGE_DB_PATH";

    private readonly string dbPath = Path.Combine(Path.GetTempPath(), $"forge-test-{Guid.NewGuid():N}.db");
    private readonly Dictionary<string, string?> extraConfig;

    public ForgeWebApplicationFactory() : this(null) { }

    internal ForgeWebApplicationFactory(Dictionary<string, string?>? extraConfig)
    {
        this.extraConfig = extraConfig ?? new();
        // Belt and suspenders #1: env var takes effect during
        // WebApplicationBuilder construction, before Program.cs reads any
        // config. ConfigureAppConfiguration alone is too late because
        // Program top-level statements may have already created bootstrap
        // resources (e.g. SQLite journal-mode pragma) by then.
        Environment.SetEnvironmentVariable(EnvVarName, this.dbPath);
        Environment.SetEnvironmentVariable("Forge__Db__Path", this.dbPath);
    }

    /// <summary>
    /// forge_implementation.md Phase 28 — convenience constructor that flips
    /// <c>Forge:Stories:OrchestratorEnabled=true</c> for end-to-end tests
    /// driving the spawn lifecycle through real crew artifact production.
    /// AutoProgressGates is left off so existing tests can validate each gate
    /// independently (they still PATCH /spawn/phase manually).
    /// </summary>
    public static ForgeWebApplicationFactory WithOrchestratorEnabled() =>
        new(new Dictionary<string, string?>
        {
            ["Forge:Stories:OrchestratorEnabled"] = "true",
            ["Forge:Stories:AutoProgressGates"] = "false",
        });

    /// <summary>
    /// Orchestrator + auto-progress flag both on. Mirrors the real dev / local
    /// runtime where the Plan-PR and decomposition-approval gates collapse so
    /// the operator sees the full lifecycle without manual merges.
    /// </summary>
    public static ForgeWebApplicationFactory WithAutoProgressGates() =>
        new(new Dictionary<string, string?>
        {
            ["Forge:Stories:OrchestratorEnabled"] = "true",
            ["Forge:Stories:AutoProgressGates"] = "true",
        });

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                // Belt and suspenders #2: explicit in-memory source. Wins
                // when Program.cs reads via DI after Build().
                ["Forge:Db:Path"] = this.dbPath,
                // Phase 8.10 — auto-clone shells out to `git clone`, which
                // doesn't make sense in CI / unit tests. Workstream-create
                // tests assume the auto-clone path is a no-op.
                ["Forge:Repo:AutoClone"] = "false",
                // Surface unhandled exception detail in tests so 500 responses
                // include the actual stack trace in the body.
                ["DetailedErrors"] = "true",
                ["Logging:LogLevel:Default"] = "Error",
                // forge_implementation.md Phase 28 — by default tests exercise
                // the gate-only (manual-API) flow. End-to-end orchestrator tests
                // override this to "true" via the dedicated factory ctor.
                ["Forge:Stories:OrchestratorEnabled"] = "false",
            };
            foreach (var kv in this.extraConfig)
            {
                settings[kv.Key] = kv.Value;
            }
            config.AddInMemoryCollection(settings);
        });

        // Belt and suspenders #3: tripwire that runs during Build(). The
        // resolved Forge:Db:Path MUST be the per-test temp file. If anything
        // else is in scope, fail loud — a passing test against the prod DB
        // is much worse than a failing one.
        builder.ConfigureServices((ctx, services) =>
        {
            var resolved = ctx.Configuration["Forge:Db:Path"];
            if (string.IsNullOrEmpty(resolved))
            {
                throw new InvalidOperationException(
                    "Forge:Db:Path is empty in test factory. The test config override didn't apply.");
            }
            if (string.Equals(resolved, ProdDbDefault, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Forge:Db:Path resolved to the production default '{ProdDbDefault}' inside a test. " +
                    $"This means an earlier read in Program.cs already created the prod connection. " +
                    $"Move all config reads after Build() / DI resolution.");
            }
            if (!resolved.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Forge:Db:Path '{resolved}' is not under %TEMP%. Tests MUST run against an isolated temp DB.");
            }
            if (!string.Equals(resolved, this.dbPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Forge:Db:Path '{resolved}' does not match this factory's per-test path '{this.dbPath}'.");
            }
        });

        // Override real story-spawn DI bindings with test-friendly
        // implementations so tests don't hit real git, LLMs, or providers.
        builder.ConfigureTestServices(services =>
        {
            var artifactDesc = ServiceDescriptor.Singleton<IStoryArtifactStrategy, DeterministicStoryArtifactStrategy>();
            var gitDesc = ServiceDescriptor.Singleton<IStoryGitOps, InMemoryStoryGitOps>();
            var provDesc = ServiceDescriptor.Singleton<IStoryProviderOps, LocalStoryProviderOps>();
            services.Add(artifactDesc);
            services.Add(gitDesc);
            services.Add(provDesc);
        });
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // Clear the env vars so a follow-up factory instance gets a clean slate.
        Environment.SetEnvironmentVariable(EnvVarName, null);
        Environment.SetEnvironmentVariable("Forge__Db__Path", null);
        if (disposing && File.Exists(this.dbPath))
        {
            try { File.Delete(this.dbPath); }
            catch (IOException) { /* file lock — best effort */ }
        }
    }
}
