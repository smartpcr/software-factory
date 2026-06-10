// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using Forge.Api.Agents;
using Forge.Api.Auth;
using Forge.Api.Domain;
using Forge.Api.Endpoints;
using Forge.Api.Hosting;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Providers.Ado;
using Forge.Api.Providers.GitHub;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Forge.Api.Services.Git;
using Microsoft.EntityFrameworkCore;

// Pin every descendant of this process to a Windows JobObject with
// KILL_ON_JOB_CLOSE before any agent code runs. If Forge.Api dies unclean,
// the OS reaps every child copilot.exe / claude.exe / git.exe with us, so
// they can't pile up as orphans across restarts.
ChildProcessTracker.Initialize();

// Capture the PRISTINE process environment NOW — before MSBuildLocator
// (below) or anything else mutates it — so every child process Forge spawns
// (Copilot CLI/SDK, dotnet build/test/restore, …) gets a clean, fresh-shell
// environment instead of inheriting Forge.Api's SDK-resolver pollution.
// See ChildProcessEnv for why this prevents the MSB4276 build failures that
// burned the agent's entire wall-clock budget at score 0.
ChildProcessEnv.CaptureBaseline();

// ── Elevated-broker mode ──────────────────────────────────────────────────
// The launcher starts a SECOND Forge.Api.exe with --elevated-broker as
// Administrator (one UAC prompt). That instance runs ONLY the named-pipe test
// broker (no web host), so the MAIN Forge.Api runs non-elevated — every git /
// agent / normal-build child is non-elevated — and only the build/test command
// of a RunTestsElevated repo is marshalled to this broker to run as admin.
// --broker-shutdown: tell a running elevated broker to exit, then quit. Run
// (non-elevated) by run-forge-ui.ps1 -Stop so the detached elevated broker is
// cleaned up gracefully without an elevated taskkill / UAC prompt.
if (Array.Exists(args, a => string.Equals(a, Forge.Api.Agents.ElevatedCommandBroker.ShutdownArg, StringComparison.Ordinal)))
{
    using var shutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await Forge.Api.Agents.ElevatedCommandBroker.RequestShutdownAsync(shutCts.Token);
    return;
}

if (Array.Exists(args, a => string.Equals(a, Forge.Api.Agents.ElevatedCommandBroker.ModeArg, StringComparison.Ordinal)))
{
    using var brokerCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; brokerCts.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => brokerCts.Cancel();

    // --watch-pid <pid>: exit when the (non-elevated) Forge.Api web host this
    // broker serves exits. That way a plain non-elevated `-Stop` (which kills
    // the host) also tears down this elevated broker — no separate elevated
    // stop, and a `-Rebuild` restart cleanly replaces both.
    var watchIdx = Array.IndexOf(args, "--watch-pid");
    if (watchIdx >= 0 && watchIdx + 1 < args.Length
        && int.TryParse(args[watchIdx + 1], out var watchPid) && watchPid > 0)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var host = System.Diagnostics.Process.GetProcessById(watchPid);
                await host.WaitForExitAsync(brokerCts.Token);
            }
            catch { /* host already gone / bad pid */ }
            brokerCts.Cancel();
        });
    }

    await Forge.Api.Agents.ElevatedCommandBroker.RunServerAsync(brokerCts.Token);
    return;
}

// ── Global crash diagnostics ──────────────────────────────────────────────
// Log unhandled exceptions that would otherwise kill the process silently.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var ex = e.ExceptionObject as Exception;
    Console.Error.WriteLine($"[Forge] FATAL UnhandledException (isTerminating={e.IsTerminating}): {ex}");
    Console.Error.Flush();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    Console.WriteLine($"[Forge] ProcessExit at {DateTime.UtcNow:O}");

    // Last-ditch synchronous kill of the SDK-spawned copilot.exe tree.
    // ProcessExit gets ~3s before the OS terminates us, so we MUST stay
    // synchronous and avoid DI / SDK async calls. CopilotServerManager
    // updates LastSpawnedPid via Interlocked when it spawns/disposes the
    // shared server, so this handler is non-zero whenever a server is
    // currently alive. Graceful host shutdown calls
    // CopilotServerManager.StopAsync first which sets LastSpawnedPid to
    // 0 and reaps the tree explicitly; this handler is the panic path
    // (Ctrl-C / unhandled exception / IDE stop / parent-shell exit).
    var pid = System.Threading.Interlocked.Exchange(
        ref Forge.Api.Agents.CopilotServerManager.LastSpawnedPid, 0);
    if (pid <= 0) return;
    try
    {
        using var proc = System.Diagnostics.Process.GetProcessById(pid);
        if (!proc.HasExited)
        {
            proc.Kill(entireProcessTree: true);
            Console.WriteLine($"[Forge] ProcessExit: killed shared Copilot server PID {pid} (entire tree).");
        }
    }
    catch (ArgumentException)
    {
        // PID already gone — nothing to do.
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Forge] ProcessExit: kill of PID {pid} failed: {ex.Message}");
    }
};

// Phase 59 Stage C — register the MSBuild instance for Roslyn's
// MSBuildWorkspace BEFORE any code that might touch it. Must run
// before `WebApplication.CreateBuilder` because the ASP.NET host
// loads assemblies that have static Roslyn references (analyzers,
// source generators) and MSBuildLocator throws if it sees Roslyn
// types already loaded with a different MSBuild than the one we
// register. Defensive try/catch — a missing SDK shouldn't crash
// boot; the RoslynImpactedTestProvider's CanHandleAsync will
// simply return false and the chain falls through to the
// heuristic.
try
{
    if (!Microsoft.Build.Locator.MSBuildLocator.IsRegistered)
    {
        Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(
        $"[Forge] MSBuildLocator.RegisterDefaults failed: {ex.Message}. " +
        "RoslynImpactedTestProvider will be disabled; impacted-test " +
        "selection falls back to the heuristic for C# workspaces.");
}

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5180");

// JSON: camelCase + ISO-8601 timestamps.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// CORS: allow the Vite dev server on :5181 in Development.
const string DevCorsPolicy = "ForgeUiDev";
builder.Services.AddCors(options =>
{
    options.AddPolicy(DevCorsPolicy, policy => policy
        .WithOrigins("http://localhost:5181", "http://127.0.0.1:5181")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

// EF Core: SQLite, path overridable via Forge:Db:Path. The path MUST be
// resolved inside the factory delegate so the test factory's
// `WebApplicationFactory.ConfigureWebHost` overrides have a chance to land
// (those callbacks run during `builder.Build()` — config reads ABOVE that
// see only `appsettings.json` + env vars, which is how earlier integration
// tests polluted the production DB at `E:\forge\forge.db`).
{
    // Run the journal_mode bootstrap once per process, on first DbContext
    // resolution. `bootstrapped` is keyed on the resolved path so a different
    // path (e.g. tests) bootstraps separately.
    var bootstrapped = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var bootstrapLock = new object();

    const string ProdDbDefault = @"E:\forge\forge.db";

    // Defense-in-depth against tests writing to the production DB. The
    // factory tripwire in `ForgeWebApplicationFactory.ConfigureServices`
    // catches the case where `Forge:Db:Path` resolves to the prod default,
    // but it's been bypassed twice now (most recently 2026-05-03 — 18 stale
    // crews + 12 stale agents had to be scrubbed). This second layer trips
    // when Program.cs is hosting inside a test process and somehow ends up
    // pointed at the prod path anyway. Two independent signals:
    //   1. An xUnit (or Microsoft.AspNetCore.TestHost) assembly is loaded
    //      in the AppDomain — a test runner is hosting us.
    //   2. The `FORGE_TEST_GUARDRAIL=1` env var is set — `ForgeWebApplicationFactory`
    //      sets this in its ctor as a one-way flag (set to "1" only, never
    //      unset, so parallel xUnit tests don't race on it the way they
    //      raced on the old per-test `FORGE_DB_PATH` value).
    static bool IsHostedInTestProcess()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("FORGE_TEST_GUARDRAIL"), "1", StringComparison.Ordinal))
            return true;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = a.GetName().Name;
            if (name is null) continue;
            if (name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(name, "Microsoft.AspNetCore.TestHost", StringComparison.Ordinal)) return true;
            if (string.Equals(name, "Microsoft.AspNetCore.Mvc.Testing", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // Phase A — IForgeDbPaths is registered as a singleton so consumers
    // (Program.cs, the 11 endpoint sites, CrewStoryArtifactStrategy, the
    // Phase B/F migration tooling) all resolve paths through the same
    // service instead of inlining `?? @"E:\forge\forge.db"` fallbacks.
    // See docs/forge/split-db.md §"Phase A".
    builder.Services.AddSingleton<Forge.Api.Persistence.IForgeDbPaths, Forge.Api.Persistence.ForgeDbPaths>();

    // Phase B — splitter dry-run analyzer. Singleton because it holds no
    // per-request state and is invoked from an admin endpoint that's
    // gated behind a feature flag (Forge:Db:SplitEnabled).
    builder.Services.AddSingleton<Forge.Api.Persistence.Split.SplitDryRunService>();

    // Phase F — split apply service. Mutates forge.db on POST
    // /api/admin/db/split-apply (PO-role + Forge:Db:SplitEnabled gated).
    // Singleton: no per-request state.
    builder.Services.AddSingleton<Forge.Api.Persistence.Split.SplitApplyService>();

    // Phase G.4 — single dispatcher for all Phase{N}Migration.ApplyAsync calls.
    // Routes by PhaseMigrationCatalog.Kind: Control migrations run once
    // against the control DB; Product/Mixed migrations also fan out to each
    // per-product DB that exists post-Phase-F.
    builder.Services.AddSingleton<Forge.Api.Persistence.PhaseMigrationRunner>();

    // Phase G — legacy ForgeDbContext class deleted. The split
    // ControlDbContext + ProductDbContext (+ IProductDbContextFactory)
    // below cover every consumer that used to take the union context.

    // Phase D — ControlDbContext binds to the same forge.db file as the
    // legacy union context did during the migration window. After Phase F,
    // this remains the only context bound to forge.db (everything else
    // moves to per-product files via IProductDbContextFactory).
    builder.Services.AddDbContext<Forge.Api.Persistence.ControlDbContext>((sp, opt) =>
    {
        var paths = sp.GetRequiredService<Forge.Api.Persistence.IForgeDbPaths>();
        var dbPath = paths.ControlDbPath();
        if (string.Equals(dbPath, ProdDbDefault, StringComparison.OrdinalIgnoreCase) && IsHostedInTestProcess())
        {
            throw new InvalidOperationException(
                $"ControlDbContext refused to open the production Forge DB ('{ProdDbDefault}') from a test process. " +
                $"This indicates the test factory's Forge:Db:Path override did not reach Program.cs's " +
                $"AddDbContext delegate (the factory adds an in-memory IConfiguration source via " +
                $"ConfigureWebHost.ConfigureAppConfiguration; if that source is being shadowed by " +
                $"appsettings.json, the override silently fails). Inspect ForgeWebApplicationFactory " +
                $"and the IConfiguration source order before re-running tests.");
        }

        // WAL + synchronous=NORMAL bootstrap (Phase A; moved here from the
        // legacy union-context registration as part of Phase D step-5
        // cleanup). The `bootstrapped` HashSet is keyed on the resolved
        // path so a different path (tests) bootstraps separately.
        bool needBootstrap;
        lock (bootstrapLock)
        {
            needBootstrap = bootstrapped.Add(dbPath);
        }
        if (needBootstrap)
        {
            Console.WriteLine($"[Forge] SQLite DB path: {dbPath}");
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            cmd.ExecuteNonQuery();
        }
        opt.UseSqlite($"Data Source={dbPath};Default Timeout=30");
    });

    // Phase D — per-request factory for ProductDbContext. Scoped so the
    // per-request cache disposes at request end. IHttpContextAccessor is
    // already registered transitively by AddSignalR / AddControllers but
    // we register it explicitly here to make the dependency obvious.
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<Forge.Api.Persistence.IProductDbContextFactory, Forge.Api.Persistence.ProductDbContextFactory>();
    // Phase D — make ProductDbContext directly resolvable from request DI
    // so endpoints can take it as a parameter (e.g. (ControlDbContext control,
    // ProductDbContext db)). The factory picks the right per-product file based
    // on the X-Forge-Product-Id header; during Phase D interim, every context
    // binds to forge.db until Phase F lays down per-product files.
    builder.Services.AddScoped<Forge.Api.Persistence.ProductDbContext>(sp =>
        sp.GetRequiredService<Forge.Api.Persistence.IProductDbContextFactory>().CreateForCurrentRequest());

    // Phase E — fan-out helper for background workers that need to
    // iterate every product (or a "due" subset) and process per-product
    // work via the right ProductDbContext. Scoped so tests can swap
    // ControlDbContext + factory independently.
    builder.Services.AddScoped<Forge.Api.Persistence.ProductFanout>();

    // Phase 80.B — resolves repo rows now that the full repo lives in the
    // per-product DB. Routes repo_registry → owning product's DB. Scoped so it
    // shares the request's ControlDbContext + product factory cache.
    builder.Services.AddScoped<Forge.Api.Persistence.RepoLookup>();
}

// Prevent a single BackgroundService failure from tearing down the host.
builder.Services.Configure<HostOptions>(opts =>
{
    opts.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

// OpenAPI / Swagger.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Forge API",
        Version = "v1",
        Description = "REST API for the Forge software-factory dashboard.",
    });
});

// SignalR.
builder.Services.AddSignalR();

// Background-services kill switch. Tests set FORGE_TEST_GUARDRAIL=1 in the
// test factory's constructor (which runs BEFORE WebApplicationBuilder
// hits this line). When set, hosted services like CrewQueueWorker /
// ProviderSyncService / WorkItemSyncJob / BuildSyncJob / AutoMergeWatcher /
// etc. are REGISTERED in DI (tests can resolve them and call RunOnceAsync
// deterministically) but DO NOT auto-start. This stops them from racing
// with test setup — they used to wake up on the seeded atlas-platform
// repo and write sync runs / build artifact rows that broke ~300
// unrelated tests. Prod leaves the env var unset so all services start.
//
// We deliberately read the env var (set in the test factory ctor) rather
// than `builder.Configuration["Forge:Background:Disabled"]`. The factory's
// in-memory config source isn't merged into IConfiguration until Build()
// runs — but AddHostedService is called BEFORE Build(), so a config-based
// flag would always read its default (false) at registration time.
var enableBackground = Environment.GetEnvironmentVariable("FORGE_TEST_GUARDRAIL") != "1";

// Git worktree service.
builder.Services.Configure<GitWorktreeOptions>(builder.Configuration.GetSection(GitWorktreeOptions.SectionName));
builder.Services.AddSingleton<IGitWorktreeService, GitWorktreeService>();

// Phase 44 — agent busy/idle slot tracking.
builder.Services.AddSingleton<AgentSlotService>();

// Phase 8.10 — repo auto-clone service.
builder.Services.Configure<RepoCloneOptions>(builder.Configuration.GetSection(RepoCloneOptions.SectionName));
builder.Services.AddSingleton<IRepoCloneService, RepoCloneService>();

// Phase 5 — provider abstraction + ADO/GitHub implementations + sync.
builder.Services.Configure<AdoOptions>(builder.Configuration.GetSection(AdoOptions.SectionName));
builder.Services.Configure<GitHubOptions>(builder.Configuration.GetSection(GitHubOptions.SectionName));
builder.Services.Configure<ProviderSyncOptions>(builder.Configuration.GetSection(ProviderSyncOptions.SectionName));
builder.Services.AddHttpClient(AdoProvider.ClientName);
builder.Services.AddHttpClient(GitHubProvider.ClientName);
// Phase 10 — ADO config resolver (user-settings → appsettings).
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IAdoConfigResolver, AdoConfigResolver>();
builder.Services.AddSingleton<IForgeProvider, AdoProvider>();
builder.Services.AddSingleton<IForgeProvider, GitHubProvider>();
// Phase 15.2 / 15.4 — sprint discovery services (ADO classification + GH milestones).
builder.Services.AddSingleton<IAdoClassificationService, AdoClassificationService>();
builder.Services.AddSingleton<IGitHubMilestoneService, GitHubMilestoneService>();
builder.Services.AddSingleton<WebhookActivity>();
builder.Services.AddScoped<ProviderCache>();
builder.Services.AddSingleton<ProviderSyncService>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<ProviderSyncService>());
// Phase 12.4 — work item sync. Singleton so the in-memory per-repo throttle
// dictionary survives across requests; uses IServiceScopeFactory internally
// to resolve a fresh ProductDbContext per sync call.
builder.Services.AddSingleton<WorkItemSyncService>();

// Phase 23 — scheduled work-item sync job (20-min cadence). Singleton
// registration is unconditional so tests can resolve it directly via
// `IServiceProvider.GetRequiredService<WorkItemSyncJob>()`; the
// AddHostedService wrapper is gated so it doesn't auto-start in test mode.
builder.Services.AddSingleton<Forge.Api.Services.WorkItemSyncJob>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.WorkItemSyncJob>());

// Phase 6 — write-side: human-gate, auto-merge watcher, audit + idempotency, user context.
builder.Services.Configure<HumanGateOptions>(builder.Configuration.GetSection(HumanGateOptions.SectionName));
builder.Services.AddSingleton<IHumanGateEvaluator, HumanGateEvaluator>();
// Phase 14.4 — generator↔evaluator iteration orchestrator.
// Phase 16.4 — gets the live TaskChannel so iteration boundaries emit on task.{wid}.
builder.Services.AddScoped<Forge.Api.Services.IDeliverableLoop>(sp =>
    new Forge.Api.Services.DeliverableLoop(
        sp.GetRequiredService<Forge.Api.Persistence.ControlDbContext>(),
        sp.GetRequiredService<Forge.Api.Persistence.ProductDbContext>(),
        sp.GetService<Forge.Api.Realtime.TaskChannel>()));
// Phase 14.7 — promotion / demotion runner (called from the admin endpoint).
builder.Services.AddScoped<Forge.Api.Services.IPromotionRunner, Forge.Api.Services.PromotionRunner>();
// Phase 91 — nightly auto-promotion scheduler (02:00 PT). Promotion-only,
// gated by avg > AutoPromoteMinAvg + throughput >= AutoPromoteMinThroughput
// on top of the tier verdict. Singleton so tests can resolve it and drive
// RunOnceAsync deterministically; the AddHostedService wrapper is gated so it
// doesn't auto-fire in test mode.
builder.Services.Configure<Forge.Api.Services.PromotionSchedulerOptions>(
    builder.Configuration.GetSection(Forge.Api.Services.PromotionSchedulerOptions.SectionName));
builder.Services.AddSingleton<Forge.Api.Services.PromotionScheduler>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.PromotionScheduler>());
// Phase 55 (docs/forge/sloppy_guard.md) — cross-iteration memory store.
// Scoped so it shares the caller's DbContext / transaction; producer for
// the Memory handle on AgentContext consumed by the runner cells in
// Phases C/D/E.
builder.Services.AddScoped<Forge.Api.Services.AgentMemoryStore>();
builder.Services.AddScoped<Forge.Api.Services.WorkstreamMemoryContextStore>();
// Phase 82 — durable per-iteration prompt audit store. Scoped so it
// shares the caller's ProductDbContext / transaction; mirrors every
// task.prompt emit in CrewStoryArtifactStrategy so the audit trail
// survives the TranscriptRetentionJanitor (last-10-iters open /
// last-1 closed) and the TaskChannel 10 000-row cap.
builder.Services.AddScoped<Forge.Api.Services.IterationPromptStore>();
// Phase 55 / Phase F — Claude/SDK memory compactor. Scoped because it
// shares the AgentMemoryStore's scope; depends on IAgentRunner (singleton)
// and IModelRegistry (singleton) which are safe to capture across scopes.
// The model-window clamp ratio is operator-tunable via
// Forge:Compaction:ContextWindowRatio (default 0.5 — see CompactionOptions).
builder.Services.Configure<Forge.Api.Services.CompactionOptions>(
    builder.Configuration.GetSection(Forge.Api.Services.CompactionOptions.SectionName));
builder.Services.AddScoped<Forge.Api.Services.MemoryCompactor>();
// Phase 55 / Phase G — periodic reaper for stale agent_memory rows.
// Singleton hosted service; sweeps hourly by default and deletes rows
// whose UpdatedAt crossed Forge:Memory:RetentionDays.
builder.Services.AddSingleton<Forge.Api.Services.AgentMemoryReaper>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.AgentMemoryReaper>());
// Periodic reaper for stale per-workstream git worktrees. Removes worktrees
// for {planning, cancelled, historic, complete} workstreams plus per-WS
// orphans whose branch matches no live row — the only event-driven cleanup
// (orchestrator merge transition) misses workstreams that reach complete via
// any other path, so worktrees otherwise accumulate without bound.
builder.Services.AddSingleton<Forge.Api.Services.WorktreeReaper>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.WorktreeReaper>());
// 2026-06-04 — workstream silent-stall detector. Periodically scans for
// workstreams in Status=active + ExecutionStatus=idle for >IdleThreshold with
// no open queue row, no live runner, no recent transcript/deliverable, and
// no unmet upstream dep. Flips to stalled with a structured StallReason so
// the operator can recover via the existing /restart endpoint. Detection-
// only in v1 — auto-restart deferred until an idempotent EnqueueAsync path
// is proven. See WorkstreamLivenessMonitor.cs for the rationale (two real
// incidents on 2026-06-04: pri-postgres 4h26m undetected idle,
// end-to-end-backfill-scenario ~3 days undetected idle).
builder.Services.AddSingleton<Forge.Api.Services.WorkstreamLivenessMonitor>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.WorkstreamLivenessMonitor>());
// Phase 16.7 — per-work-item live channel (transcript persistence + SignalR fan-out).
builder.Services.AddSingleton<Forge.Api.Realtime.TaskChannel>();
// Phase 16.8 — worktree change feed (one FileSystemWatcher per active workstream).
builder.Services.AddSingleton<Forge.Api.Services.WorktreeChangeFeed>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.WorktreeChangeFeed>());
// Phase 16.3 — auto-delegation hosted service (lead → crew, ≤10 s p95).
builder.Services.AddSingleton<Forge.Api.Services.LeadAutoDispatcher>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.LeadAutoDispatcher>());
// Phase 17.2 — task queue service. Scoped so it shares the request's DbContext.
builder.Services.AddScoped<Forge.Api.Services.ITaskQueueService, Forge.Api.Services.TaskQueueService>();
// Drag-to-reconcile inspector for the Exec board. Computes the authoritative
// execution status from runner + queue + workstream + deliverable; the
// /api/work-items/{id}/execution-status endpoint accepts the operator's
// drag-move only when target == authoritative. Scoped — shares the
// request's ProductDbContext.
builder.Services.AddScoped<Forge.Api.Services.WorkItemExecutionStatusInspector>();
// Phase 17.4 — queue worker hosted service (boot scan + 5s tick).
builder.Services.AddSingleton<Forge.Api.Services.CrewQueueWorker>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.CrewQueueWorker>());
// Phase 51 — per-task budget-cap cooldown reconciler (15s tick).
// Auto-resumes work items parked at ExecutionStatus="cooldown-budget"
// when (a) the operator raises the crew's MaxTaskBudgetUsd so
// cumulative cost is back under cap, or (b) the cooldown window
// (default 10m) elapses — the latter rotates the pair to a fresh
// generator/evaluator via WorkItemRow.LastPairAgentIdsJson.
builder.Services.Configure<Forge.Api.Services.BudgetCooldownOptions>(
    builder.Configuration.GetSection(Forge.Api.Services.BudgetCooldownOptions.SectionName));
builder.Services.AddSingleton<Forge.Api.Services.BudgetCooldownReconciler>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.BudgetCooldownReconciler>());
// Phase 52 — sloppy-iteration guard reconciler. Picks up work items
// parked at ExecutionStatus="pending-reassign" and either rotates them
// to a fresh pair (with full clean-slate wipe) or marks them
// terminal-failed when PairAttemptCount has hit MaxPairAttempts. 15s
// tick by default; tests drop the interval and drive RunOnceAsync.
builder.Services.Configure<Forge.Api.Services.SloppyReassignmentOptions>(
    builder.Configuration.GetSection(Forge.Api.Services.SloppyReassignmentOptions.SectionName));
builder.Services.AddSingleton<Forge.Api.Services.SloppyReassignmentReconciler>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.SloppyReassignmentReconciler>());
// 2026-05-15 — auto-recovery sweep for workstreams wrongly demoted from
// `complete`/`merging` to `stalled` by a stale sloppy-reassignment tick.
// Root cause fixed in SloppyReassignmentReconciler.MarkTerminalFailedAsync;
// this reconciler is the belt-and-suspenders cleanup that re-promotes any
// stalled row whose PR is in `merged`. Idempotent; ticks every 60 s.
builder.Services.AddSingleton<Forge.Api.Services.StalledMergedRecoveryReconciler>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.StalledMergedRecoveryReconciler>());
// 2026-05-27 — merged-PR re-score recovery. When a workstream's PR merged but
// its latest deliverable is a non-pass (the generator hit its wall-clock
// budget and the loop force-scored 0), the evaluator scores the LANDED code so
// the work item can close on a real score instead of the operator Restarting
// merged work into the same timeout forever. Gated by
// Forge:Stories:MergedPrRescoreEnabled (default on). Ticks every 120 s.
builder.Services.AddSingleton<Forge.Api.Services.MergedPrRescoreReconciler>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.MergedPrRescoreReconciler>());
// Phase 29 — async build validation worker.
builder.Services.AddSingleton(System.Threading.Channels.Channel.CreateUnbounded<Forge.Api.Services.BuildValidationJob>());
builder.Services.AddSingleton<Forge.Api.Services.BuildValidationWorker>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.BuildValidationWorker>());
// Phase 24 — PR state & comment sync sweep.
builder.Services.AddSingleton<Forge.Api.Services.ForgeMetrics>();
builder.Services.AddHttpClient(Forge.Api.Services.AdoPrCommentSyncClient.ClientName);
builder.Services.AddHttpClient(Forge.Api.Services.GitHubPrCommentSyncClient.ClientName);
builder.Services.AddSingleton<Forge.Api.Services.IPrCommentSyncClient, Forge.Api.Services.AdoPrCommentSyncClient>();
builder.Services.AddSingleton<Forge.Api.Services.IPrCommentSyncClient, Forge.Api.Services.GitHubPrCommentSyncClient>();
builder.Services.AddSingleton<Forge.Api.Services.PrSyncJob>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.PrSyncJob>());

// forge_implementation.md Phase 27 — build sync, failure parser, artifact expander.
builder.Services.AddHttpClient(Forge.Api.Providers.Ado.AdoBuildSyncRunner.ClientName);
builder.Services.AddHttpClient(Forge.Api.Providers.GitHub.GitHubBuildSyncRunner.ClientName);
builder.Services.AddHttpClient(nameof(Forge.Api.Services.BuildArtifactExpander));
builder.Services.AddSingleton<Forge.Api.Services.IBuildSyncRunner, Forge.Api.Providers.Ado.AdoBuildSyncRunner>();
builder.Services.AddSingleton<Forge.Api.Services.IBuildSyncRunner, Forge.Api.Providers.GitHub.GitHubBuildSyncRunner>();
builder.Services.AddSingleton<Forge.Api.Services.BuildArtifactExpander>();
builder.Services.AddSingleton<Forge.Api.Services.BuildFailureParser>();
builder.Services.AddSingleton<Forge.Api.Services.BuildSyncJob>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.BuildSyncJob>());
builder.Services.AddSingleton<AutoMergeWatcher>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoMergeWatcher>());

// 2026-05-07 — transcript retention janitor. Bounds task_transcript_event
// growth (was unbounded — DB hit 2.2 GB before this landed). Runs every
// 6 hours; admin endpoint at POST /api/admin/transcripts/prune lets
// operators trigger an on-demand sweep with optional VACUUM.
builder.Services.AddSingleton<Forge.Api.Services.TranscriptRetentionJanitor>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.TranscriptRetentionJanitor>());

// forge_implementation.md Phase 28 — story-driven spawn orchestrator + deps scheduler.
builder.Services.AddSingleton<Forge.Api.Services.Stories.AgentAllocationLoader>();
// Real-implementation bindings for the story-spawn lifecycle. No fakes /
// in-memory / Local stubs in production — see memory/feedback_no_fakes_no_manual.md.
//
//   IStoryArtifactStrategy → CrewStoryArtifactStrategy (real LLM via IAgentRunner)
//   IStoryGitOps           → RealStoryGitOps (real git CLI against repo.CloneRoot)
//   IStoryProviderOps      → ProviderStoryProviderOps (real provider.CreatePullRequest / Merge)
// Phase 59 Stage C + S2 — impacted-test resolver chain. Order matters:
// ROSLYN FIRST for C# — it's the native in-process analyzer (no external
// language-server process to spawn/hang). LSP second for the ecosystems
// Roslyn can't speak (Rust / Go / Python / TS) and as a C# fall-through
// when Roslyn can't load the workspace. Heuristic last (filename + dir +
// content scan, universal fallback). The chain is a singleton so
// per-workspace caches (LSP server processes, Roslyn's Solution) survive
// across gate runs.
//
// 2026-05-26 — reordered to Roslyn-first. The prior LSP-first ordering
// existed only because Roslyn no-op'd on repos whose TestCommand carries a
// standing category `--filter` (the FRP `update` repo), so csharp-ls was the
// only narrower for them. That flaky csharp-ls server then hung on
// `textDocument/references` with no deadline and wedged stage-1.3 for hours.
// CrewStoryArtifactStrategy now splits the standing `--filter` off before
// resolution and re-merges it after (SplitStandingFilter + ApplyTestExclude-
// Filter), so Roslyn narrows those repos natively — csharp-ls is no longer
// needed for the common C# path. ImpactedTestResolverChain.PerProviderTimeout
// also bounds any provider that does hang.
builder.Services.AddSingleton<Forge.Api.Services.Stories.ImpactedTests.Roslyn.RoslynImpactedTestProvider>();
builder.Services.AddSingleton<Forge.Api.Services.Stories.ImpactedTests.Lsp.LspImpactedTestProvider>();
builder.Services.AddSingleton<Forge.Api.Services.Stories.ImpactedTests.HeuristicImpactedTestProvider>();
builder.Services.AddSingleton<Forge.Api.Services.Stories.ImpactedTests.ImpactedTestResolverChain>(sp =>
{
    // Per-provider wall-clock budget (config override:
    // Forge:Test:ImpactedResolveTimeoutSec). A provider that exceeds it is
    // cancelled and the chain falls through to the next (ultimately the
    // heuristic). Guards against a hung in-process resolver wedging an
    // iteration forever — see ImpactedTestResolverChain.DefaultPerProviderTimeout.
    var timeoutSec = sp.GetRequiredService<IConfiguration>()
        .GetValue<int?>("Forge:Test:ImpactedResolveTimeoutSec");
    var timeout = timeoutSec is > 0
        ? TimeSpan.FromSeconds(timeoutSec.Value)
        : (TimeSpan?)null;
    return new Forge.Api.Services.Stories.ImpactedTests.ImpactedTestResolverChain(
        new Forge.Api.Services.Stories.ImpactedTests.IImpactedTestProvider[]
        {
            sp.GetRequiredService<Forge.Api.Services.Stories.ImpactedTests.Roslyn.RoslynImpactedTestProvider>(),
            sp.GetRequiredService<Forge.Api.Services.Stories.ImpactedTests.Lsp.LspImpactedTestProvider>(),
            sp.GetRequiredService<Forge.Api.Services.Stories.ImpactedTests.HeuristicImpactedTestProvider>(),
        },
        sp.GetService<ILogger<Forge.Api.Services.Stories.ImpactedTests.ImpactedTestResolverChain>>(),
        timeout);
});

builder.Services.AddSingleton<Forge.Api.Services.Stories.IStoryArtifactStrategy, Forge.Api.Services.Stories.CrewStoryArtifactStrategy>();
builder.Services.AddSingleton<Forge.Api.Services.Stories.IStoryGitOps, Forge.Api.Services.Stories.RealStoryGitOps>();
builder.Services.AddSingleton<Forge.Api.Services.Stories.IStoryProviderOps, Forge.Api.Services.Stories.ProviderStoryProviderOps>();
builder.Services.AddSingleton<Forge.Api.Services.Stories.StorySpawnOrchestrator>();
builder.Services.AddSingleton<Forge.Api.Services.Stories.SpawnDependencyScheduler>();
// Phase 42 — keeps WorkstreamRow/WorkItemRow in lockstep with implementation-plan.md.
builder.Services.AddSingleton<Forge.Api.Services.Stories.PlanReconciler>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.Stories.SpawnDependencyScheduler>());

// Phase 41 — boot-time sweep that re-fires plan-doc Resume loops killed by
// a previous process exit. Reads StorySpawnRow.ResumePendingJson and re-fires
// the gen↔eval loop for each unfinished key. Registered as a singleton so
// integration tests can resolve it and call SweepAsync deterministically;
// only auto-started when enableBackground (production runtime).
builder.Services.AddSingleton<Forge.Api.Services.Stories.StoryResumeRecoveryService>();
if (enableBackground) builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.Stories.StoryResumeRecoveryService>());

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserContext, HttpUserContext>();

// Phase 7.6 — pluggable auth (default = single-user with optional shared secret).
builder.Services.Configure<ForgeAuthOptions>(builder.Configuration.GetSection(ForgeAuthOptions.SectionName));

// Phase 9 — interactive MSAL sign-in for ADO (WAM-first, DPAPI token cache).
builder.Services.AddSingleton<IBrowserLauncher, BrowserLauncher>();
builder.Services.AddSingleton<IAuthService, AdoAuthService>();
builder.Services.AddSingleton<IAuthService, GitHubAuthService>();

// Phase 11 — agent runtime. AgentRunnerDispatcher routes invocations across
// the four (backend × mode) cells: Copilot/SDK (default), Copilot/CLI,
// Claude/SDK (Anthropic /v1/messages with streaming SSE + soul.md prompt cache),
// Claude/CLI (claude --print). The local-stub model bypasses every backend
// for offline tests. Tool calls go through the sandboxed AgentToolHost (Phase 11.6).
builder.Services.AddSingleton<IModelRegistry, ModelRegistry>();
builder.Services.AddSingleton<IAgentToolHost, AgentToolHost>();
builder.Services.Configure<AgentRunnerOptions>(builder.Configuration.GetSection(AgentRunnerOptions.SectionName));
builder.Services.Configure<SoulOptions>(builder.Configuration.GetSection(SoulOptions.SectionName));
builder.Services.AddSingleton<ISoulService, SoulService>();
builder.Services.AddHttpClient(AgentRunnerDispatcher.ClientName);
builder.Services.AddSingleton<CopilotServerManager>();
// CopilotServerManager registered as a hosted service unconditionally —
// its StartAsync is a no-op (the CLI server starts lazily on first use)
// but its StopAsync owns disposing the spawned `copilot.exe` child. Without
// hosted-service registration, host shutdown skips StopAsync, the child
// process leaks, and a later GC-driven Process callback hits a disposed
// HasExited check that crashes the test host. The dispose path runs whether
// or not the CLI server was ever started, so the test gate doesn't apply.
builder.Services.AddHostedService(sp => sp.GetRequiredService<CopilotServerManager>());
// 2026-05-30 — overlays the authoritative Copilot model catalog (context
// windows + reasoning support) onto the JSON-seeded IModelRegistry at boot +
// every 6h, so the registry stops drifting from reality (hand-maintained
// models.json windows were repeatedly wrong). Best-effort; JSON values stand
// if the catalog is unreachable.
builder.Services.AddHostedService<Forge.Api.Services.ModelCatalogEnrichmentService>();
// Phase 55 / Phase E (docs/forge/sloppy_guard.md) -- Copilot SDK
// session pool, keyed by (workItemId, agentId). Singleton so live
// sessions span the gen-eval loop's iterations; disposed per-work-item
// from RunGenEvalLoopAsync's finally on terminal verdict.
builder.Services.AddSingleton<CopilotSessionPool>();
builder.Services.AddSingleton<IAgentRunner, AgentRunnerDispatcher>();

// Problem details (RFC-7807). In Development, surface exception messages so
// failing endpoint calls don't return opaque 500s.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        if (ctx.HttpContext.RequestServices.GetService<IHostEnvironment>()?.IsDevelopment() == true)
        {
            var ex = ctx.HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            if (ex is not null)
            {
                ctx.ProblemDetails.Detail = ex.Message;
                ctx.ProblemDetails.Extensions["exceptionType"] = ex.GetType().FullName;
                ctx.ProblemDetails.Extensions["stackTrace"] = ex.StackTrace;
            }
        }
    };
});

var app = builder.Build();

// Ensure DB exists and is seeded (idempotent).
using (var scope = app.Services.CreateScope())
{
    var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
    var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
    // Phase G — create BOTH schemas in the same forge.db file (Phase D
    // interim mode). ControlDbContext owns global tables (forge_actions,
    // products, users, crews, agents, etc.); ProductDbContext owns
    // per-product tables (repos, workstreams, work_items, deliverables, builds, etc.).
    // EnsureCreated() is a no-op when ANY table exists in the file, so on a
    // mixed forge.db both EnsureCreated calls turn into no-ops and we rely
    // on the Phase migrations below to add any missing schema. On a fresh
    // boot we run both create scripts explicitly to lay down the union.
    await control.Database.EnsureCreatedAsync();
    // Phase 80.B — relocate `repos` from the control DB into each per-product
    // DB. Runs AFTER EnsureCreated (so the control tables, incl. `products`,
    // exist — creating any table before EnsureCreated would make it treat the
    // DB as already-initialised and skip the schema) but BEFORE the product
    // schema script below: in a split DB, forge.db still holds the control
    // `repos` table (with data), and since `repos` is now a ProductDbContext
    // entity the script would otherwise `CREATE TABLE repos` on a file that
    // already has it and crash. The move drops the control copy first. Uses its
    // own SqliteConnection (no EF), split-mode-only, marker-gated/idempotent.
    {
        var movePaths = scope.ServiceProvider.GetRequiredService<Forge.Api.Persistence.IForgeDbPaths>();
        var moveLog = scope.ServiceProvider.GetService<ILoggerFactory>()?
            .CreateLogger<Forge.Api.Persistence.RepoMoveToProductService>();
        var move = new Forge.Api.Persistence.RepoMoveToProductService(movePaths, moveLog);
        var moveResult = await move.ApplyAsync();
        if (moveResult.Errors.Count > 0)
        {
            scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("RepoMove")
                .LogError("Phase 80.B repos move reported {Count} error(s) (control `repos` left intact): {Errors}",
                    moveResult.Errors.Count, string.Join("; ", moveResult.Errors));
        }
    }
    {
        // Check if any product-side table exists (e.g. workstreams). If not,
        // execute the product-side CREATE script. This works on fresh DBs
        // (no product tables) AND on legacy union DBs (product tables already
        // exist, skip the create).
        await using var probeConn = new Microsoft.Data.Sqlite.SqliteConnection(db.Database.GetConnectionString());
        await probeConn.OpenAsync();
        await using var probeCmd = probeConn.CreateCommand();
        probeCmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='workstreams';";
        var wsExists = (long)(await probeCmd.ExecuteScalarAsync() ?? 0L) > 0;
        if (!wsExists)
        {
            var productSchemaScript = db.Database.GenerateCreateScript();
            await db.Database.ExecuteSqlRawAsync(productSchemaScript);
        }
    }
    // Phase G.4 — replace the historical 49-line explicit invocation sequence
    // with a runner that consults PhaseMigrationCatalog for routing and
    // applies migrations in PhaseMigrationRunner.ExecutionOrder. The order is
    // preserved exactly (Phase 42 first for EF-model dependencies; 43/45/46/
    // 47/49/50 before Phase 38's backfills, etc.); post-Phase-F the runner
    // additionally fans Product/Mixed migrations across every per-product DB.
    {
        var runner = scope.ServiceProvider.GetRequiredService<Forge.Api.Persistence.PhaseMigrationRunner>();
        await runner.ApplyAllAsync(control, db);
    }
    // Phase G — control-side product_runtime watermark table for the
    // "due/dirty" optimization (workers skip products with no pending
    // work without opening their per-product DB). Outside the numbered
    // Phase{N}Migration series since it's infrastructure, not a feature
    // phase. Idempotent.
    await Forge.Api.Persistence.PhaseGDueIndexMigration.ApplyAsync(control);
    // Phase 47 (spec) — reap stale agent_usage rows from a crashed runner so
    // a permanently-busy agent doesn't wedge the diversity picker.
    // Phase 48 follow-up — by definition no invocation can be in flight at
    // boot, so close ALL open rows (not just those older than the 24h cap).
    // Without this, fresh-orphaned rows from the most recent crash leave
    // their agents marked busy and plan-doc loops freeze because the
    // pair-diversity picker filters every candidate out as busy.
    await Forge.Api.Services.BusyAgentsHelper.ReapAllOpenOnBootAsync(control);
    // Phase 29 — cancel orphaned validation runs left from a previous crash/restart.
    {
        var now = DateTime.UtcNow.ToString("O");
        var orphaned = await db.BuildValidationRuns
            .Where(r => r.Status == "queued" || r.Status == "running")
            .ToListAsync();
        foreach (var r in orphaned)
        {
            r.Status = "cancelled";
            r.FinishedAt = now;
            r.ErrorMessage = "Server restarted — run was not completed";
        }
        if (orphaned.Count > 0) await db.SaveChangesAsync();
    }
    // Phase 44 — recover orphaned agent claims from a previous crash.
    {
        var slots = scope.ServiceProvider.GetRequiredService<AgentSlotService>();
        await slots.RecoverOrphanedClaimsAsync();
    }
    // Prototype data seed (cards/PRs/workstreams) is intentionally skipped —
    // real data is committed in forge.db.
    // await ForgeSeed.EnsureSeededAsync(db);
    //
    // Crew + prototype-agent catalog IS reconciled on every boot so the
    // canonical 8 crews + A1/A2/E4/E7/E9/R3/R5/Q2/XD agents stay static
    // even after past test pollution flipped fields like crew-eng.Shift.
    // The pass is idempotent: a clean DB sees zero writes.
    await ForgeSeed.EnsureCrewCatalogAsync(control, db);
    // Test factory seeds default product+repo into an otherwise empty per-
    // instance DB so test fixtures find the baseline reference rows. Safe
    // here because Forge:Background:Disabled=true keeps the prod sync /
    // build / merge hosted services from auto-starting and racing on the
    // seeded rows. Prod skips this — operators have their own product /
    // repo committed in forge.db.
    if (Environment.GetEnvironmentVariable("FORGE_TEST_GUARDRAIL") == "1")
    {
        await ForgeSeed.EnsureDefaultProductAndRepoAsync(control, db);
    }
    // Phase 80.B — reconcile the control repo_registry from the per-product
    // repos rows (both split + single-file modes), set Product.DefaultRepoId,
    // and backfill null RepoId on product tables. Idempotent; runs after the
    // physical move + any default-repo seed so it sees every repo.
    {
        var regFactory = scope.ServiceProvider.GetRequiredService<Forge.Api.Persistence.IProductDbContextFactory>();
        var regLog = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("RepoRegistrySync");
        await Forge.Api.Persistence.RepoRegistrySync.ReconcileAsync(control, regFactory, regLog);
    }
    // Board columns backfill — the Phase 80 per-product DB split left some
    // product DBs (e.g. local/raft/failover-cluster) without the canonical
    // board columns, so their Plan & Design board rendered empty (zero columns
    // → zero cells → no cards visible). Idempotent: products that already have
    // columns are untouched.
    {
        var colFactory = scope.ServiceProvider.GetRequiredService<Forge.Api.Persistence.IProductDbContextFactory>();
        var colLog = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("ForgeSeed.BoardColumns");
        var seeded = await ForgeSeed.EnsureBoardColumnsAsync(control, colFactory, colLog);
        if (seeded > 0) colLog?.LogInformation("Boot backfill seeded board columns into {Count} product DB(s).", seeded);
    }
    // Phase 13c — every agent should belong to a team. The reconcile pass
    // is idempotent: subsequent restarts no-op once the roster is filled.
    await Forge.Api.Endpoints.CrewsEndpoints.ApplyAutoAssignAsync(control, db, caller: "boot", ct: default);
    // Repair pass: any workstream stuck on review/merging whose PR is
    // already merged gets snapped to "complete". Backfills rows merged
    // before WorkstreamMergeAdvancer was wired into the merge paths.
    // Phase 80 split-db follow-up: run this over EVERY product DB, not just
    // the current/default ProductDbContext. Otherwise a code-intelligence row
    // can have PrRow.Col="merged" while WorkstreamRow.Status stays "review"
    // forever because terminal PRs have IsActive=false and PrSyncJob will not
    // revisit them.
    {
        var fanout = scope.ServiceProvider.GetRequiredService<Forge.Api.Persistence.ProductFanout>();
        var log = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("WorkstreamMergeAdvancer");
        var advancedTotal = 0;
        await fanout.ForEachProductAsync(async (fanoutControl, productDb, slug, ct) =>
        {
            var advanced = await Forge.Api.Services.Stories.WorkstreamMergeAdvancer.BackfillAsync(fanoutControl, productDb, ct);
            if (advanced > 0)
            {
                System.Threading.Interlocked.Add(ref advancedTotal, advanced);
                log?.LogInformation("Boot backfill advanced {Count} workstream(s) to complete on already-merged PRs in product {ProductSlug}.", advanced, slug);
            }
        });
        if (advancedTotal > 0)
        {
            log?.LogInformation("Boot backfill advanced {Count} workstream(s) to complete on already-merged PRs across product DBs.", advancedTotal);
        }
    }
    // Repair pass: any active PR whose parent story spawn has AutoMerge=true
    // but whose PrRow.AutoMerge=false gets the flag flipped on. Repairs PRs
    // created before SpawnAutoMergeResolver was wired into every PR-creation
    // path; without this, existing PRs sit on Col=review forever because
    // AutoMergeWatcher only iterates `WHERE AutoMerge=true`.
    {
        var touched = await Forge.Api.Services.Stories.SpawnAutoMergeResolver.BackfillPrsAsync(control, db);
        if (touched > 0)
        {
            scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("SpawnAutoMergeResolver")
                .LogInformation("Boot backfill enabled AutoMerge on {Count} PR row(s) inheriting from their parent story spawn.", touched);
        }
    }
    // Repair pass: force PrSyncJob to re-probe every non-merged PR on its
    // first tick (10s after startup). Two conditions are normalised:
    //   1. IsActive=true, Col!=merged — the common case. Reset LastSyncAt so
    //      the cadence gate (default 7200s) doesn't filter recently-synced
    //      rows whose upstream state has since changed.
    //   2. IsActive=false, Col!=merged — orphaned rows from a half-applied
    //      sync (e.g. IsActive flipped before Col, or an explicit endpoint
    //      cleared IsActive without finishing). Re-flag them active +
    //      LastSyncStatus=idle so the sweep picks them up.
    {
        var stalePrs = await db.Prs
            .Where(p => p.Col != "merged")
            .ToListAsync();
        var touched = 0;
        foreach (var p in stalePrs)
        {
            if (p.LastSyncAt is not null) { p.LastSyncAt = null; touched++; }
            if (!p.IsActive) { p.IsActive = true; touched++; }
            if (p.LastSyncStatus == "running") { p.LastSyncStatus = "idle"; touched++; }
        }
        if (touched > 0)
        {
            await db.SaveChangesAsync();
            scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("PrSyncBootRefresh")
                .LogInformation("Boot normalised {Count} PR row(s) so PrSyncJob's next sweep re-probes upstream state.", stalePrs.Count);
        }
    }
    // Startup config: enforce canonical model/quota/evaluator-model on
    // all 8 crews and guarantee each day/night shift has ≥ 2 members.
    var startupLogger = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("ForgeSeed");
    var startupConfig = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var backendPolicy = Forge.Api.Configuration.ForgeBackendPolicy.FromConfiguration(startupConfig);
    await ForgeSeed.EnsureStartupConfigAsync(control, db, startupLogger, backendPolicy);
}

// One-time CLI scrub of test fixture rows that leaked into the prod DB
// before the test factory's tripwire landed. Default is dry-run; pass
// --apply to actually delete. Exits without starting the web host so it
// can run while Forge.Api is otherwise idle.
//   dotnet run --project src/forge/Forge.Api -- --scrub-test-artifacts
//   dotnet run --project src/forge/Forge.Api -- --scrub-test-artifacts --apply
if (args.Contains("--scrub-test-artifacts"))
{
    var apply = args.Contains("--apply");
    using var scrubScope = app.Services.CreateScope();
    var scrubControl = scrubScope.ServiceProvider.GetRequiredService<ControlDbContext>();
    var scrubDb = scrubScope.ServiceProvider.GetRequiredService<ProductDbContext>();
    var scrubLogger = scrubScope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("ForgeSeed.Scrub");
    var report = await ForgeSeed.ScrubTestArtifactsAsync(scrubControl, scrubDb, dryRun: !apply, scrubLogger);

    Console.WriteLine($"[Scrub] mode={(apply ? "APPLIED" : "DRY-RUN")}");
    Console.WriteLine($"  crews deleted:                    {report.CrewsDeleted}");
    if (report.CrewIdsDeleted.Count > 0)
    {
        Console.WriteLine($"    {string.Join(", ", report.CrewIdsDeleted)}");
    }
    Console.WriteLine($"  agents deleted:                   {report.AgentsDeleted}");
    if (report.AgentIdsDeleted.Count > 0)
    {
        Console.WriteLine($"    {string.Join(", ", report.AgentIdsDeleted)}");
    }
    Console.WriteLine($"  crew_members deleted:             {report.CrewMembersDeleted}");
    Console.WriteLine($"  crew_history deleted:             {report.CrewHistoryDeleted}");
    Console.WriteLine($"  agent_history deleted:            {report.AgentHistoryDeleted}");
    Console.WriteLine($"  agent_usage deleted:              {report.AgentUsageDeleted}");
    Console.WriteLine($"  agent_level_history deleted:      {report.AgentLevelHistoryDeleted}");
    Console.WriteLine($"  deliverables deleted:             {report.DeliverablesDeleted}");
    Console.WriteLine($"  task_queue.routed_to_crew nulled: {report.TaskQueueRoutedNulled}");
    Console.WriteLine($"  task_queue.preferred_agent nulled:{report.TaskQueuePreferredNulled}");
    Console.WriteLine($"  agent_assignments.crew_id nulled: {report.AgentAssignmentCrewNulled}");
    if (!apply)
    {
        Console.WriteLine();
        Console.WriteLine("DRY-RUN — no changes written. Re-run with --apply to commit.");
    }
    return;
}

// One-time CLI scrub of mocked story spawns + workstreams left over from
// integration tests / fixture runs that wrote into the prod DB. Keeps the
// real `code-intelligence:AGENT-MEMORY` story and its 5 workstreams.
//   dotnet run --project src/forge/Forge.Api -- --scrub-mocked-spawns
//   dotnet run --project src/forge/Forge.Api -- --scrub-mocked-spawns --apply
if (args.Contains("--scrub-mocked-spawns"))
{
    var apply = args.Contains("--apply");
    using var spawnScope = app.Services.CreateScope();
    var spawnControl = spawnScope.ServiceProvider.GetRequiredService<ControlDbContext>();
    var spawnDb = spawnScope.ServiceProvider.GetRequiredService<ProductDbContext>();
    var spawnLogger = spawnScope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("ForgeSeed.SpawnScrub");

    // The single real story spawn currently in the DB. If you need to keep
    // additional spawns, extend this list before re-running.
    var keepSpawns = new[] { "spawn-code-intelligence-AGENT-MEMORY" };
    // Empty here = all kept workstreams come from the kept spawns. Add ad-hoc
    // workstream ids (no spawn) to this list to preserve them.
    var keepWorkstreams = Array.Empty<string>();

    var report = await ForgeSeedSpawnScrub.ScrubMockedSpawnsAsync(
        spawnControl, spawnDb, keepSpawns, keepWorkstreams, dryRun: !apply, spawnLogger);

    Console.WriteLine($"[SpawnScrub] mode={(apply ? "APPLIED" : "DRY-RUN")}");
    Console.WriteLine($"  story_spawns deleted:    {report.StorySpawnsDeleted}");
    Console.WriteLine($"  workstreams deleted:     {report.WorkstreamsDeleted}");
    Console.WriteLine($"  workstream_deps deleted: {report.WorkstreamDepsDeleted}");
    Console.WriteLine();
    Console.WriteLine($"  kept spawns: {string.Join(", ", keepSpawns)}");
    Console.WriteLine();
    if (report.StorySpawnIdsDeleted.Count > 0)
    {
        Console.WriteLine("  spawns to delete:");
        foreach (var id in report.StorySpawnIdsDeleted) Console.WriteLine($"    {id}");
    }
    if (report.WorkstreamIdsDeleted.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("  workstreams to delete:");
        foreach (var id in report.WorkstreamIdsDeleted) Console.WriteLine($"    {id}");
    }
    if (!apply)
    {
        Console.WriteLine();
        Console.WriteLine("DRY-RUN — no changes written. Re-run with --apply to commit.");
    }
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();

// Phase 7.6 — auth gate runs before idempotency/audit so unauthorized writes
// never enter the cache and never produce an audit row.
app.UseMiddleware<ForgeSecretMiddleware>();

// Phase D — resolves X-Forge-Product-Id / ?productId= into a product slug
// stashed on HttpContext.Items so the IProductDbContextFactory can pick
// the right per-product DB without re-querying. Runs AFTER the auth /
// secret middleware (so the resolved user is available) and BEFORE the
// audit middleware (so the per-product write context is set up).
app.UseMiddleware<Forge.Api.Persistence.ProductScopeMiddleware>();
// Phase 80.E — resolve the active repo (X-Forge-Repo-Id). Runs right after the
// product-scope middleware so it can supplement the product slug (a repo id
// alone resolves + binds its owning product DB) and stash the active repo id
// for per-endpoint repo filtering.
app.UseMiddleware<Forge.Api.Persistence.RepoScopeMiddleware>();
// Phase 6.12 — replay cached responses on duplicate Idempotency-Key (writes only).
// Phase 6.10 — audit every write to forge_actions.
// Order matters: idempotency wraps audit so replays are NOT re-audited.
app.UseMiddleware<IdempotencyMiddleware>();
app.UseMiddleware<AuditMiddleware>();

// Always serve the OpenAPI document so `npm run gen:api` works against any env.
// Path is /openapi.json (mapped via RouteTemplate). UseSwaggerUI stays dev-only.
app.UseSwagger(c => c.RouteTemplate = "{documentName}/openapi.json");

if (app.Environment.IsDevelopment())
{
    app.UseCors(DevCorsPolicy);
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/v1/openapi.json", "Forge API v1"));
}

app.MapHealthEndpoints();
app.MapStateEndpoints();
app.MapCardsEndpoints();
app.MapTimelineEndpoints();
app.MapWorkstreamsEndpoints();
// forge_implementation.md Phase 28 — story-driven spawn endpoints.
app.MapStoriesEndpoints();
app.MapPrsEndpoints();
app.MapProvidersEndpoints();
app.MapWebhooksEndpoints();
app.MapAuditEndpoints();
app.MapHumanGateEndpoints();
app.MapProductsEndpoints();
app.MapSprintEndpoints();
app.MapAuthEndpoints();
app.MapMeEndpoints();
app.MapMeSettingsEndpoints();
app.MapCrewsEndpoints();
app.MapBackendPolicyEndpoints();
app.MapModelsEndpoints();
app.MapAgentsEndpoints();
app.MapWorkItemsEndpoints();
// Phase 14.5 — deliverable read + dispute endpoints.
app.MapDeliverablesEndpoints();
// Phase 14.7 — promotion run + level-history.
app.MapPromotionEndpoints();
// Phase 14.8 — HR payroll + /spend deprecation alias.
app.MapPayrollEndpoints();
// Phase 16.9 — live execution snapshot + transcript pagination.
app.MapTaskExecutionEndpoints();
// Phase 17.5 — persistent task queue surface.
app.MapQueueEndpoints();
// Phase 17 follow-up — admin diagnostics (manual worker tick).
app.MapAdminEndpoints();
// Phase B — splitter dry-run admin endpoint. Gated by both the
// product-owner role and the Forge:Db:SplitEnabled config flag.
app.MapAdminDbSplitEndpoints();
// Phase 22 — assigned-agent + assignment history endpoints.
app.MapAgentAssignmentsEndpoints();
// Phase 24 — PR comment CRUD + resolve/reopen endpoints.
app.MapPrCommentsEndpoints();
// forge_implementation.md Phase 27 — builds REST surface.
app.MapBuildsEndpoints();
// Phase 11 — /cost dashboard rollup.
app.MapCostEndpoints();
app.MapPerformanceEndpoints();

app.MapHub<ForgeHub>("/hubs/forge");

// Serve agent avatar PNGs from the UI assets directory so PR descriptions
// on ADO can embed real character portraits via http://localhost:5180/api/avatars/{file}.
app.MapGet("/api/avatars", () =>
{
    var assetsDir = Path.Combine(
        ForgeSeed.FindRepoRoot(), "src", "forge", "Forge.UI", "src", "assets");
    if (!Directory.Exists(assetsDir)) return Results.Ok(Array.Empty<string>());
    var files = Directory.GetDirectories(assetsDir)
        .SelectMany(dir => Directory.GetFiles(dir, "*.png"))
        .Select(Path.GetFileName)
        .Where(f => f is not null)
        .OrderBy(f => f)
        .ToList();
    return Results.Ok(files);
}).WithTags("Avatars").WithName("ListAvatars");

app.MapGet("/api/avatars/{filename}", (string filename) =>
{
    if (filename.Contains("..") || filename.Contains('/') || filename.Contains('\\'))
        return Results.BadRequest();
    var assetsDir = Path.Combine(
        ForgeSeed.FindRepoRoot(), "src", "forge", "Forge.UI", "src", "assets");
    // Search all subfolders for the file.
    var filePath = Directory.GetDirectories(assetsDir)
        .Select(dir => Path.Combine(dir, filename))
        .FirstOrDefault(File.Exists);
    if (filePath is null) return Results.NotFound();
    return Results.File(filePath, "image/png");
}).WithTags("Avatars").WithName("GetAgentAvatar");

// Prevent unobserved Task exceptions from crashing the process.
// Background agent tasks (Task.Run in CrewQueueWorker) can throw
// ObjectDisposedException on shutdown — log and swallow instead of crashing.
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.Error.WriteLine($"[Forge] UnobservedTaskException: {e.Exception}");
    e.SetObserved();
};

// Checkpoint SQLite WAL on shutdown so data survives forced stop.
app.Lifetime.ApplicationStopping.Register(() =>
{
    using var scope = app.Services.CreateScope();
    var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
    var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
    db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE)");
    Console.WriteLine("[Forge] WAL checkpoint completed.");
});

app.Run();

/// <summary>
/// Sentinel for <c>WebApplicationFactory&lt;Program&gt;</c> in integration tests.
/// </summary>
public partial class Program { }
