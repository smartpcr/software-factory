// -----------------------------------------------------------------------
// <copyright file="ForgeWebApplicationFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Infrastructure;

using System.Linq;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Test factory that gives each test instance a private temp <b>folder</b>
/// containing the control DB and any per-product DBs that get materialised.
/// Tests run against fully isolated state and NEVER touch the production
/// DB folder (<c>E:\forge</c>).
///
/// <para>
/// <b>Phase G.3:</b> The factory uses <c>Forge:Db:Folder</c> (not the
/// legacy <c>Forge:Db:Path</c>) so the path resolver produces a per-test
/// control DB at <c>{folder}\forge.db</c> and per-product DBs at
/// <c>{folder}\{slug}.db</c>. Multi-product fixtures can exercise true
/// fan-out semantics by physicalising per-product DBs inside the same
/// folder.
/// </para>
/// <para>
/// Setup: a fresh GUID-suffixed folder is created in <c>%TEMP%</c>.
/// Program.cs's EnsureCreatedAsync + ForgeSeed.EnsureCrewCatalogAsync
/// build the schema and seed canonical crews+agents on first boot.
/// Tests that need other reference rows (products, repos, work items)
/// seed them explicitly.
/// </para>
/// <para>
/// Teardown: the entire folder is removed in <see cref="Dispose"/>.
/// </para>
/// <para>
/// The DB folder override uses ONLY the in-memory <c>IConfiguration</c>
/// source — no process-global env var manipulation. This eliminates the
/// race condition that caused the original production DB leak (parallel
/// xUnit tests stomping each other's <c>FORGE_DB_PATH</c> env var).
/// Two post-build tripwires assert (1) the resolved control DB path is
/// under this instance's per-test folder and (2) the resolved product DB
/// paths are also under that folder — so a misconfigured Phase F split
/// can't quietly write per-product DBs into the prod folder.
/// </para>
/// </summary>
public class ForgeWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string ProdFolderDefault = @"E:\forge";
    private const string ProdDbDefault = @"E:\forge\forge.db";

    private readonly string folder = Path.Combine(Path.GetTempPath(), $"forge-test-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> extraConfig;

    /// <summary>
    /// Per-instance temp folder containing the control DB and any
    /// per-product DBs. All DB files Forge.Api creates during this test
    /// run live inside this directory.
    /// </summary>
    protected internal string Folder => this.folder;

    /// <summary>
    /// Per-instance control DB path. Subclasses (e.g. e2e factories that
    /// wire real <c>IStoryGitOps</c>) read this to locate the parent state
    /// dir where production code resolves <c>storyworkspaces/{spawnId}</c>
    /// and <c>attachments/{spawnId}</c> under the configured DB folder.
    /// </summary>
    protected internal string DbPath => Path.Combine(this.folder, ForgeDbPaths.ControlDbFileName);

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
        // Pre-create the folder so ForgeDbPaths' Directory.CreateDirectory
        // call is a no-op (its ctor would otherwise create the folder lazily).
        Directory.CreateDirectory(this.folder);
        // Each test gets an EMPTY temp folder — Program.cs's EnsureCreatedAsync
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
            // appsettings.json sets this to false (production fire-and-forget).
            // Tests assert PlanPrId is populated as soon as POST returns, so
            // the drive must complete inline.
            ["Forge:Stories:SpawnDriveInline"] = "true",
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
            ["Forge:Stories:SpawnDriveInline"] = "true",
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
                // Phase G.3 — point ForgeDbPaths at the per-test folder. The
                // control DB resolves to {folder}\forge.db and per-product DBs
                // resolve to {folder}\{slug}.db. Program.cs reads this inside
                // the AddDbContext factory delegate at DI resolution time
                // (after Build()), so the in-memory override always wins.
                ["Forge:Db:Folder"] = this.folder,
                // Explicitly null out the legacy Forge:Db:Path key. Otherwise
                // an appsettings.json or env-var setting could shadow the
                // folder override and force the control DB onto an
                // unexpected file path.
                ["Forge:Db:Path"] = null,
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
                // docs/forge/e2e.md §2.1 — pin the orchestrator integration
                // tests to the legacy parallel flow (impl waits on prior impl
                // only). Production defaults this ON; the gate's own behaviour
                // is covered by ImplGatesOnPriorStageE2eTests. A test that
                // wants the gated flow can override via extraConfig.
                ["Forge:Stories:GateImplOnPriorStageE2e"] = "false",
                // Keep prod hosted services (CrewQueueWorker, ProviderSyncService,
                // WorkItemSyncJob, BuildSyncJob, AutoMergeWatcher, etc.) registered
                // in DI but not auto-started. Tests that need them call
                // RunOnceAsync deterministically; tests that don't need them
                // aren't fighting their background writes.
                ["Forge:Background:Disabled"] = "true",
                // Phase 95 — the chat bridge is opt-in; pin it OFF for tests so
                // the suite never inherits a real appsettings.json Enabled=true
                // (the operator machine's config). Tests that exercise the bridge
                // set Enabled=true explicitly via extraConfig.
                ["Forge:ChatBridge:Enabled"] = "false",
            };
            foreach (var kv in this.extraConfig)
            {
                settings[kv.Key] = kv.Value;
            }
            config.AddInMemoryCollection(settings);
        });

        // Tripwires that run during Build(). Two layers of defense:
        //   1. The resolved control DB path MUST be under this factory's
        //      per-test folder.
        //   2. The resolved per-product DB path for an arbitrary slug MUST
        //      also be under this factory's per-test folder (catches
        //      mis-routing in ProductDbPath where a Phase F split could
        //      otherwise write per-product DBs into the prod folder).
        // If either tripwire fires, fail loud — a passing test against the
        // prod DB is much worse than a failing one.
        builder.ConfigureServices((ctx, services) =>
        {
            // Probe IForgeDbPaths via a transient resolution so tests can't
            // accidentally rely on a different resolver. We build a tiny
            // throwaway provider with the current configuration.
            var probeServices = new ServiceCollection();
            probeServices.AddSingleton<IConfiguration>(ctx.Configuration);
            probeServices.AddSingleton<IForgeDbPaths, ForgeDbPaths>();
            using var probe = probeServices.BuildServiceProvider();
            var paths = probe.GetRequiredService<IForgeDbPaths>();

            var controlPath = paths.ControlDbPath();
            if (string.IsNullOrEmpty(controlPath))
            {
                throw new InvalidOperationException(
                    "ForgeDbPaths.ControlDbPath() returned empty inside the test factory. " +
                    "The Forge:Db:Folder override didn't apply.");
            }
            if (string.Equals(controlPath, ProdDbDefault, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"ControlDbPath resolved to the production default '{ProdDbDefault}' inside a test. " +
                    $"This means an earlier read in Program.cs already created the prod connection. " +
                    $"Move all config reads after Build() / DI resolution.");
            }
            if (!controlPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"ControlDbPath '{controlPath}' is not under %TEMP%. Tests MUST run against an isolated temp folder.");
            }
            if (!controlPath.StartsWith(this.folder, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"ControlDbPath '{controlPath}' is not under this factory's per-test folder '{this.folder}'.");
            }

            // Phase G.3 — also tripwire per-product paths. Use a sentinel
            // slug that ForgeDbPaths.TryValidateSlug accepts (it's [a-z0-9-]
            // and non-reserved). If ProductDbPath ever escapes the folder,
            // catch it here.
            const string ProbeSlug = "tripwire-probe";
            var productPath = paths.ProductDbPath(ProbeSlug);
            if (!productPath.StartsWith(this.folder, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"ProductDbPath('{ProbeSlug}') resolved to '{productPath}', which is not under " +
                    $"this factory's per-test folder '{this.folder}'. Per-product DBs would leak " +
                    $"outside the test sandbox; refusing to boot.");
            }
            if (string.Equals(paths.Folder, ProdFolderDefault, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"ForgeDbPaths.Folder resolved to the production default '{ProdFolderDefault}' " +
                    $"inside a test. Override Forge:Db:Folder via in-memory configuration.");
            }
        });

        // Override real story-spawn DI bindings with test-friendly
        // implementations so tests don't hit real git, LLMs, or providers.
        // Use RemoveAll + Add (rather than just Add) so the production
        // descriptor is removed entirely. With both descriptors in the
        // collection the resolver still picks the last one, but extra
        // hosted-service eager-init and IEnumerable<T> consumers can end
        // up constructing the production singleton with a stale scope —
        // which manifested as ObjectDisposedException on the first
        // OpenPrAsync call inside the orchestrator's plan phase.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IStoryArtifactStrategy>();
            services.RemoveAll<IStoryGitOps>();
            services.RemoveAll<IStoryProviderOps>();
            services.AddSingleton<IStoryArtifactStrategy, DeterministicStoryArtifactStrategy>();
            services.AddSingleton<IStoryGitOps, InMemoryStoryGitOps>();
            services.AddSingleton<IStoryProviderOps, LocalStoryProviderOps>();
        });
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(this.folder))
        {
            try { Directory.Delete(this.folder, recursive: true); }
            catch (IOException) { /* file lock — best effort */ }
            catch (UnauthorizedAccessException) { /* file lock — best effort */ }
        }
    }
}

