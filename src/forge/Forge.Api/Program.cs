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
};

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

    builder.Services.AddDbContext<ForgeDbContext>((sp, opt) =>
    {
        var cfg = sp.GetRequiredService<IConfiguration>();
        var dbPath = cfg["Forge:Db:Path"] ?? ProdDbDefault;
        if (!Path.IsPathFullyQualified(dbPath))
            dbPath = Path.GetFullPath(dbPath);

        if (string.Equals(dbPath, ProdDbDefault, StringComparison.OrdinalIgnoreCase) && IsHostedInTestProcess())
        {
            throw new InvalidOperationException(
                $"Refusing to open the production Forge DB ('{ProdDbDefault}') from a test process. " +
                $"This indicates the test factory's Forge:Db:Path override did not reach Program.cs's " +
                $"AddDbContext delegate (the factory adds an in-memory IConfiguration source via " +
                $"ConfigureWebHost.ConfigureAppConfiguration; if that source is being shadowed by " +
                $"appsettings.json, the override silently fails). Inspect ForgeWebApplicationFactory " +
                $"and the IConfiguration source order before re-running tests.");
        }

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
            cmd.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;";
            cmd.ExecuteNonQuery();
        }
        // Phase 48 follow-up — `Default Timeout=30` makes SQLite wait up to
        // 30 s for a lock before throwing "database is locked". The
        // orchestrator's tick loop and the queue worker both write to the
        // shared file; without the timeout, two concurrent writers race
        // and the second one fails immediately. The user hit this on the
        // failover-cluster:XRAFT spawn at 17:36 PT 2026-05-09 ("plan
        // failed: SQLite Error 5: 'database is locked'") because the
        // regression-to-Plan write coincided with a concurrent operation.
        opt.UseSqlite($"Data Source={dbPath};Default Timeout=30");
    });
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
// to resolve a fresh ForgeDbContext per sync call.
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
        sp.GetRequiredService<Forge.Api.Persistence.ForgeDbContext>(),
        sp.GetService<Forge.Api.Realtime.TaskChannel>()));
// Phase 14.7 — promotion / demotion runner (called from the admin endpoint).
builder.Services.AddScoped<Forge.Api.Services.IPromotionRunner, Forge.Api.Services.PromotionRunner>();
// Phase 55 (docs/forge/sloppy_guard.md) — cross-iteration memory store.
// Scoped so it shares the caller's DbContext / transaction; producer for
// the Memory handle on AgentContext consumed by the runner cells in
// Phases C/D/E.
builder.Services.AddScoped<Forge.Api.Services.AgentMemoryStore>();
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
    var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
    await db.Database.EnsureCreatedAsync();
    // Phase 42 must run BEFORE any migration that issues an EF query against
    // workstreams / work_items. EF's SELECT lists every mapped column on
    // WorkstreamRow / WorkItemRow — including the Phase 42 additions — so an
    // existing DB without those columns throws SQLITE 'no such column' as
    // soon as e.g. Phase 38's backfill calls db.WorkItems.ToListAsync().
    // The migration's pragma probe is a no-op when the columns already exist
    // (fresh DB created by EnsureCreatedAsync), so running it first is safe.
    await Phase42Migration.ApplyAsync(db);
    // Phase 8.2 — additive schema upgrade for existing dev DBs that pre-date Phase 8.
    await Phase8Migration.ApplyAsync(db);
    // Phase 11.4 — agent_usage cost ledger.
    await Phase11Migration.ApplyAsync(db);
    // Phase 12.2 — work_items + work_item_links.
    await Phase12Migration.ApplyAsync(db);
    // Phase 14.1 — agent level/hourly + crew schedule + deliverables/level_history tables.
    await Phase14Migration.ApplyAsync(db);

    await Phase15Migration.ApplyAsync(db);
    // Phase 16.1 — pair columns + auto-dispatch + cost-category + transcript table.
    await Phase16Migration.ApplyAsync(db);
    // Phase 17.1 — persistent task queue + history + idempotency guard.
    await Phase17Migration.ApplyAsync(db);
    // Phase 18 — per-crew Shift column ('day' | 'night').
    await Phase18Migration.ApplyAsync(db);
    // Phase 19 — execution status + trial count on work items.
    await Phase19Migration.ApplyAsync(db);
    // Phase 20 — max-iteration override on task_queue rows.
    await Phase20Migration.ApplyAsync(db);
    // Phase 21 — SkipSoulPrompt flag on task_queue rows.
    await Phase21Migration.ApplyAsync(db);
    // Phase 22 — AssignedAgent field + agent_assignments history + backfill.
    await Phase22Migration.ApplyAsync(db);
    // Phase 23 — scheduled work-item sync columns + sync-runs table.
    await Phase23Migration.ApplyAsync(db);
    // Phase 24 — PR sync + comment lifecycle tables and columns.
    await Phase24Migration.ApplyAsync(db);
    // Phase 25 — sync run item details.
    await Phase25Migration.ApplyAsync(db);
    // Phase 26 — build/test gate columns on repos.
    await Phase26Migration.ApplyAsync(db);

    await Phase27Migration.ApplyAsync(db);
    // Phase 28 — PR diff stats (Updates column on prs table).
    await Phase28Migration.ApplyAsync(db);
    // Phase 29 — init/restore commands + build validation run history.
    await Phase29Migration.ApplyAsync(db);
    // Phase 24 Q24.14 — preferred-agent pin on task_queue + livelock counter on work_items.
    await Phase30Migration.ApplyAsync(db);
    // forge_implementation.md Phase 27 — build sync + repair loop tables.
    await Phase31Migration.ApplyAsync(db);
    // forge_implementation.md Phase 28 — story spawns + workstream dependency edges.
    await Phase32Migration.ApplyAsync(db);
    // Phase 33 — persisted sync settings on repos.
    await Phase33Migration.ApplyAsync(db);
    // Phase 34 — per-repo sync cadence overrides + upstream-changedate cursor.
    await Phase34Migration.ApplyAsync(db);
    // Phase 35 — decouple crews from products (null out ProductId).
    await Phase35Migration.ApplyAsync(db);
    // Phase 36 — add Reaction column to pr_comments.
    await Phase36Migration.ApplyAsync(db);
    // Phase 37 — add LastError + LastErrorAt columns to story_spawns so the
    // orchestrator can persist failure reasons and the UI can surface them.
    await Phase37Migration.ApplyAsync(db);
    // Phase 38 — backfill WorkItemRow rows for legacy plan-doc and
    // workstream ids so DeliverableLoop.PointsToMaxIter has a real
    // wi.Points to read from.
    // Phase 43 + Phase 45 must run first — they add columns to story_spawns
    // (AttachmentsJson, BaseBranch) which Phase 38's EF query reads via the
    // mapped entity. Skipping either would crash with "no such column".
    await Phase43Migration.ApplyAsync(db);
    await Phase45Migration.ApplyAsync(db);
    await Phase46Migration.ApplyAsync(db);
    // Phase 46 (spec) — workstreams.DepsJson + PhaseAnchor for v2 plan
    // layout. Filename retained as Phase47Migration on disk because
    // Phase46Migration is taken by the AutoMerge flag migration. MUST
    // run before Phase 38 (which issues `db.Workstreams.ToListAsync()`)
    // so the EF column projection finds DepsJson on the live DB —
    // observed 2026-05-09 boot crash on the prod DB at E:\forge\forge.db
    // ("SQLite Error 1: 'no such column: w.DepsJson'") because the prod
    // DB pre-dates the entity-model addition and Phase 47 was scheduled
    // after Phase 38.
    await Phase47Migration.ApplyAsync(db);
    // Phase 49 (spec) — workstreams.StallReason for the new stalled
    // triage state (build-gate / no-progress / non-pass verdict).
    // MUST run before Phase 38 for the same reason as Phase 47: Phase
    // 38 issues `db.Workstreams.ToListAsync()` which projects every
    // mapped column including StallReason. Without this ordering the
    // prod DB crashes at boot with "no such column: w.StallReason".
    await Phase49Migration.ApplyAsync(db);
    // Phase 50 — DecisionPinsJson on story_spawns for operator-pinned
    // decisions that break cross-doc convergence loops. MUST also run
    // before Phase 38 for the same reason: Phase38Migration reads
    // story_spawns via EF which projects DecisionPinsJson. Single
    // nullable column; safe alongside the other story_spawns migrations.
    await Phase50Migration.ApplyAsync(db);
    // Phase 51 — BudgetCooldownUntilUtc + LastPairAgentIdsJson on
    // work_items so the BudgetCooldownReconciler can auto-resume tasks
    // parked by a stalled-budget verdict (cap raise → immediate, 10m
    // elapsed → resume with pair rotation). Two nullable columns +
    // an ExecutionStatus index for the 15-second scan. Order: anywhere
    // before the reconciler boots is fine; placed next to Phase 50 to
    // keep the budget/cooldown story local.
    await Phase51Migration.ApplyAsync(db);
    // Phase 52 — sloppy-iteration guard. Adds PairAttemptCount /
    // PriorPairsJson / FailureReason on work_items and the
    // agent_pair_assignment table. Order matters: must run BEFORE
    // SloppyReassignmentReconciler boots its first tick (the reconciler
    // queries the new columns).
    await Phase52Migration.ApplyAsync(db);
    // Phase 53 — sequential plan-doc tasks. Adds PlanDocPrsJson on
    // story_spawns so each plan doc (architecture / tech-spec /
    // implementation-plan / e2e-scenarios) carries its own branch + PR
    // state and runs in strict sequence. Legacy spawns keep PlanPrId/
    // PlanBranch and use the original single-PR path.
    await Phase53Migration.ApplyAsync(db);
    await Phase54Migration.ApplyAsync(db);
    // Phase 55 (docs/forge/sloppy_guard.md) — agent_memory table for
    // cross-iteration continuity (Layer 2 of the gen-eval memory plan).
    // Order: any time before the runners boot is fine; placed adjacent to
    // 53/54 so the post-Phase-52 migration sequence stays grouped.
    await Phase55Migration.ApplyAsync(db);
    await Phase38Migration.ApplyAsync(db);
    // Phase 39 — create the deliverable_questions table for the
    // open-questions wizard.
    await Phase39Migration.ApplyAsync(db);
    // Phase 40 — backfill WorkItemRow rows for legacy story ids so the
    // ParentId on plan-docs / workstreams resolves to a real parent.
    await Phase40Migration.ApplyAsync(db);
    // Phase 41 — ResumePendingJson column on story_spawns so a plan-doc
    // Resume kicked off via /spawn/artifacts/{key}/resume survives a
    // process restart (StoryResumeRecoveryService re-fires unfinished keys).
    await Phase41Migration.ApplyAsync(db);
    // Phase 43 — AttachmentsJson on story_spawns for operator-uploaded reference docs.
    await Phase43Migration.ApplyAsync(db);
    // Phase 44 — agent_claims table + MaxConcurrentTasks on agents.
    await Phase44Migration.ApplyAsync(db);
    // Phase 47 (spec) — task_queue.RoutedEvaluatorAgentId for pair-diversity
    // (filename Phase48Migration on disk; see migration class doc).
    await Phase48Migration.ApplyAsync(db);
    // Phase 47 (spec) — reap stale agent_usage rows from a crashed runner so
    // a permanently-busy agent doesn't wedge the diversity picker.
    // Phase 48 follow-up — by definition no invocation can be in flight at
    // boot, so close ALL open rows (not just those older than the 24h cap).
    // Without this, fresh-orphaned rows from the most recent crash leave
    // their agents marked busy and plan-doc loops freeze because the
    // pair-diversity picker filters every candidate out as busy.
    await Forge.Api.Services.BusyAgentsHelper.ReapAllOpenOnBootAsync(db);
    // Phase 45 ran above (out of numeric order) so Phase 38's EF read of
    // story_spawns sees the BaseBranch column.
    // Phase 42 already ran at the top of this scope(must precede the EF
    // queries in Phase 38 / Phase 40 that select all WorkItem / Workstream
    // columns including the new PlanAnchor / PlanContentHash).
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
    await ForgeSeed.EnsureCrewCatalogAsync(db);
    // Test factory seeds default product+repo into an otherwise empty per-
    // instance DB so test fixtures find the baseline reference rows. Safe
    // here because Forge:Background:Disabled=true keeps the prod sync /
    // build / merge hosted services from auto-starting and racing on the
    // seeded rows. Prod skips this — operators have their own product /
    // repo committed in forge.db.
    if (Environment.GetEnvironmentVariable("FORGE_TEST_GUARDRAIL") == "1")
    {
        await ForgeSeed.EnsureDefaultProductAndRepoAsync(db);
    }
    // Phase 13c — every agent should belong to a team. The reconcile pass
    // is idempotent: subsequent restarts no-op once the roster is filled.
    await Forge.Api.Endpoints.CrewsEndpoints.ApplyAutoAssignAsync(db, caller: "boot", ct: default);
    // Repair pass: any workstream stuck on review/merging whose PR is
    // already merged gets snapped to "complete". Backfills rows merged
    // before WorkstreamMergeAdvancer was wired into the merge paths.
    {
        var advanced = await Forge.Api.Services.Stories.WorkstreamMergeAdvancer.BackfillAsync(db);
        if (advanced > 0)
        {
            scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("WorkstreamMergeAdvancer")
                .LogInformation("Boot backfill advanced {Count} workstream(s) to complete on already-merged PRs.", advanced);
        }
    }
    // Repair pass: any active PR whose parent story spawn has AutoMerge=true
    // but whose PrRow.AutoMerge=false gets the flag flipped on. Repairs PRs
    // created before SpawnAutoMergeResolver was wired into every PR-creation
    // path; without this, existing PRs sit on Col=review forever because
    // AutoMergeWatcher only iterates `WHERE AutoMerge=true`.
    {
        var touched = await Forge.Api.Services.Stories.SpawnAutoMergeResolver.BackfillPrsAsync(db);
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
    await ForgeSeed.EnsureStartupConfigAsync(db, startupLogger, backendPolicy);
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
    var scrubDb = scrubScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
    var scrubLogger = scrubScope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("ForgeSeed.Scrub");
    var report = await ForgeSeed.ScrubTestArtifactsAsync(scrubDb, dryRun: !apply, scrubLogger);

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
    var spawnDb = spawnScope.ServiceProvider.GetRequiredService<ForgeDbContext>();
    var spawnLogger = spawnScope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("ForgeSeed.SpawnScrub");

    // The single real story spawn currently in the DB. If you need to keep
    // additional spawns, extend this list before re-running.
    var keepSpawns = new[] { "spawn-code-intelligence-AGENT-MEMORY" };
    // Empty here = all kept workstreams come from the kept spawns. Add ad-hoc
    // workstream ids (no spawn) to this list to preserve them.
    var keepWorkstreams = Array.Empty<string>();

    var report = await ForgeSeedSpawnScrub.ScrubMockedSpawnsAsync(
        spawnDb, keepSpawns, keepWorkstreams, dryRun: !apply, spawnLogger);

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
app.MapGet("/api/avatars/{filename}", (string filename) =>
{
    if (filename.Contains("..") || filename.Contains('/') || filename.Contains('\\'))
        return Results.BadRequest();
    var crewsDir = Path.Combine(
        ForgeSeed.FindRepoRoot(), "src", "forge", "Forge.UI", "src", "assets", "crews");
    var filePath = Path.Combine(crewsDir, filename);
    if (!File.Exists(filePath)) return Results.NotFound();
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
    var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
    db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE)");
    Console.WriteLine("[Forge] WAL checkpoint completed.");
});

app.Run();

/// <summary>
/// Sentinel for <c>WebApplicationFactory&lt;Program&gt;</c> in integration tests.
/// </summary>
public partial class Program { }
