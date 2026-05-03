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

    builder.Services.AddDbContext<ForgeDbContext>((sp, opt) =>
    {
        var cfg = sp.GetRequiredService<IConfiguration>();
        var dbPath = cfg["Forge:Db:Path"] ?? @"E:\forge\forge.db";
        if (!Path.IsPathFullyQualified(dbPath))
            dbPath = Path.GetFullPath(dbPath);

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
        opt.UseSqlite($"Data Source={dbPath}");
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

// Git worktree service.
builder.Services.Configure<GitWorktreeOptions>(builder.Configuration.GetSection(GitWorktreeOptions.SectionName));
builder.Services.AddSingleton<IGitWorktreeService, GitWorktreeService>();

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
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProviderSyncService>());
// Phase 12.4 — work item sync. Singleton so the in-memory per-repo throttle
// dictionary survives across requests; uses IServiceScopeFactory internally
// to resolve a fresh ForgeDbContext per sync call.
builder.Services.AddSingleton<WorkItemSyncService>();

// Phase 23 — scheduled work-item sync job (20-min cadence).
builder.Services.AddHostedService<Forge.Api.Services.WorkItemSyncJob>();

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
// Phase 16.7 — per-work-item live channel (transcript persistence + SignalR fan-out).
builder.Services.AddSingleton<Forge.Api.Realtime.TaskChannel>();
// Phase 16.8 — worktree change feed (one FileSystemWatcher per active workstream).
builder.Services.AddSingleton<Forge.Api.Services.WorktreeChangeFeed>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.WorktreeChangeFeed>());
// Phase 16.3 — auto-delegation hosted service (lead → crew, ≤10 s p95).
builder.Services.AddSingleton<Forge.Api.Services.LeadAutoDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.LeadAutoDispatcher>());
// Phase 17.2 — task queue service. Scoped so it shares the request's DbContext.
builder.Services.AddScoped<Forge.Api.Services.ITaskQueueService, Forge.Api.Services.TaskQueueService>();
// Phase 17.4 — queue worker hosted service (boot scan + 5s tick).
builder.Services.AddSingleton<Forge.Api.Services.CrewQueueWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.CrewQueueWorker>());
// Phase 29 — async build validation worker.
builder.Services.AddSingleton(System.Threading.Channels.Channel.CreateUnbounded<Forge.Api.Services.BuildValidationJob>());
builder.Services.AddSingleton<Forge.Api.Services.BuildValidationWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.BuildValidationWorker>());
// Phase 24 — PR state & comment sync sweep.
builder.Services.AddSingleton<Forge.Api.Services.ForgeMetrics>();
builder.Services.AddHttpClient(Forge.Api.Services.AdoPrCommentSyncClient.ClientName);
builder.Services.AddHttpClient(Forge.Api.Services.GitHubPrCommentSyncClient.ClientName);
builder.Services.AddSingleton<Forge.Api.Services.IPrCommentSyncClient, Forge.Api.Services.AdoPrCommentSyncClient>();
builder.Services.AddSingleton<Forge.Api.Services.IPrCommentSyncClient, Forge.Api.Services.GitHubPrCommentSyncClient>();
builder.Services.AddSingleton<Forge.Api.Services.PrSyncJob>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.PrSyncJob>());

// forge_implementation.md Phase 27 — build sync, failure parser, artifact expander.
builder.Services.AddHttpClient(Forge.Api.Providers.Ado.AdoBuildSyncRunner.ClientName);
builder.Services.AddHttpClient(Forge.Api.Providers.GitHub.GitHubBuildSyncRunner.ClientName);
builder.Services.AddHttpClient(nameof(Forge.Api.Services.BuildArtifactExpander));
builder.Services.AddSingleton<Forge.Api.Services.IBuildSyncRunner, Forge.Api.Providers.Ado.AdoBuildSyncRunner>();
builder.Services.AddSingleton<Forge.Api.Services.IBuildSyncRunner, Forge.Api.Providers.GitHub.GitHubBuildSyncRunner>();
builder.Services.AddSingleton<Forge.Api.Services.BuildArtifactExpander>();
builder.Services.AddSingleton<Forge.Api.Services.BuildFailureParser>();
builder.Services.AddSingleton<Forge.Api.Services.BuildSyncJob>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.BuildSyncJob>());
builder.Services.AddSingleton<AutoMergeWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoMergeWatcher>());

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
builder.Services.AddHostedService(sp => sp.GetRequiredService<Forge.Api.Services.Stories.SpawnDependencyScheduler>());

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
builder.Services.AddHostedService(sp => sp.GetRequiredService<CopilotServerManager>());
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
    // Prototype data seed (cards/PRs/workstreams) is intentionally skipped —
    // real data is committed in forge.db.
    // await ForgeSeed.EnsureSeededAsync(db);
    //
    // Crew + prototype-agent catalog IS reconciled on every boot so the
    // canonical 8 crews + A1/A2/E4/E7/E9/R3/R5/Q2/XD agents stay static
    // even after past test pollution flipped fields like crew-eng.Shift.
    // The pass is idempotent: a clean DB sees zero writes.
    await ForgeSeed.EnsureCrewCatalogAsync(db);
    // Phase 13c — every agent should belong to a team. The reconcile pass
    // is idempotent: subsequent restarts no-op once the roster is filled.
    await Forge.Api.Endpoints.CrewsEndpoints.ApplyAutoAssignAsync(db, caller: "boot", ct: default);
    // Startup config: enforce canonical model/quota/evaluator-model on
    // all 8 crews and guarantee each day/night shift has ≥ 2 members.
    var startupLogger = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("ForgeSeed");
    await ForgeSeed.EnsureStartupConfigAsync(db, startupLogger);
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
