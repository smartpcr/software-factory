// -----------------------------------------------------------------------
// <copyright file="ForgeWebApplicationFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Infrastructure;

using System.Linq;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Test factory that copies the production SQLite DB to a per-instance
/// temp file so tests run against an isolated snapshot and NEVER touch
/// the production database.
///
/// <para>
/// Setup: <c>File.Copy(ProdDbDefault, tempPath)</c> — each test gets a
/// private copy of the production DB with realistic schema and data.
/// Teardown: the temp copy is deleted in <see cref="Dispose"/>.
/// </para>
/// <para>
/// The DB path override uses ONLY the in-memory <c>IConfiguration</c>
/// source — no process-global env var manipulation. This eliminates the
/// race condition that caused the original production DB leak (parallel
/// xUnit tests stomping each other's <c>FORGE_DB_PATH</c> env var).
/// A post-build tripwire asserts the resolved path is under
/// <c>%TEMP%</c> and matches this instance's copy.
/// </para>
/// </summary>
public class ForgeWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string ProdDbDefault = @"E:\forge\forge.db";

    private readonly string dbPath = Path.Combine(Path.GetTempPath(), $"forge-test-{Guid.NewGuid():N}.db");
    private readonly Dictionary<string, string?> extraConfig;

    public ForgeWebApplicationFactory() : this(null) { }

    internal ForgeWebApplicationFactory(Dictionary<string, string?>? extraConfig)
    {
        this.extraConfig = extraConfig ?? new();
        // One-way "we're in a test" signal honored by Program.cs's
        // AddDbContext delegate. Set to "1" only — never unset and never
        // varied per test, so parallel xUnit tests can't race on it the
        // way they raced on the old per-test FORGE_DB_PATH value. Program.cs
        // refuses to open the prod DB path when this flag is set.
        Environment.SetEnvironmentVariable("FORGE_TEST_GUARDRAIL", "1");
        // Each test gets an EMPTY temp DB — Program.cs's EnsureCreatedAsync
        // builds the schema and ForgeSeed.EnsureCrewCatalogAsync seeds the
        // canonical crews+agents on boot. Tests that need other reference
        // rows (products, repos, work items) seed them explicitly.
        //
        // We deliberately do NOT copy the prod DB here. Doing so caused
        // ~184 UNIQUE-constraint failures: tests that seed `WI-1035`,
        // a repo with the same RemoteUrl, etc. collided with rows that
        // already exist in the operator's prod data. "Realistic seed data"
        // is not worth the cross-test fragility.
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

        // Strip the Windows EventLog logger provider — it gets registered
        // by default on Windows and writes to a Win32 event-log handle that
        // gets disposed when the test host tears down. Fire-and-forget tasks
        // (CrewQueueWorker.RunAgentAsync) that try to log AFTER teardown
        // throw `Cannot access a disposed object. Object name: 'EventLogInternal'`,
        // which crashes the entire test process even though
        // TaskScheduler.UnobservedTaskException calls SetObserved() —
        // because the Logger.Log path rethrows synchronously from the
        // logging extension, not from a Task. Stripping it removes the
        // race entirely. Console + Debug providers stay so test output
        // still shows everything.
        builder.ConfigureLogging(logging =>
        {
            var eventLog = logging.Services
                .Where(s => s.ImplementationType?.FullName?.Contains("EventLog", System.StringComparison.Ordinal) == true)
                .ToList();
            foreach (var s in eventLog) logging.Services.Remove(s);
        });

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                // Point at the per-test copy. Program.cs reads this inside
                // the AddDbContext factory delegate at DI resolution time
                // (after Build()), so the in-memory override always wins.
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
                // Keep prod hosted services (CrewQueueWorker, ProviderSyncService,
                // WorkItemSyncJob, BuildSyncJob, AutoMergeWatcher, etc.) registered
                // in DI but not auto-started. Tests that need them call
                // RunOnceAsync deterministically; tests that don't need them
                // aren't fighting their background writes.
                ["Forge:Background:Disabled"] = "true",
            };
            foreach (var kv in this.extraConfig)
            {
                settings[kv.Key] = kv.Value;
            }
            config.AddInMemoryCollection(settings);
        });

        // Tripwire that runs during Build(). The resolved Forge:Db:Path
        // MUST be the per-test temp copy. If anything else is in scope,
        // fail loud — a passing test against the prod DB is much worse
        // than a failing one.
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
        if (disposing && File.Exists(this.dbPath))
        {
            try { File.Delete(this.dbPath); }
            catch (IOException) { /* file lock — best effort */ }
        }
    }

}

