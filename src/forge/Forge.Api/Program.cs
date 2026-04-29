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

// EF Core: SQLite, path overridable via Forge:Db:Path. Resolve in the factory
// delegate so test-factory configuration overrides flow through.
builder.Services.AddDbContext<ForgeDbContext>((sp, opt) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var dbPath = cfg["Forge:Db:Path"] ?? "forge.db";
    opt.UseSqlite($"Data Source={dbPath}");
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
builder.Services.AddSingleton<AutoMergeWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoMergeWatcher>());
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
    await ForgeSeed.EnsureSeededAsync(db);
    // Phase 13c — every agent should belong to a team. The reconcile pass
    // is idempotent: subsequent restarts no-op once the roster is filled.
    await Forge.Api.Endpoints.CrewsEndpoints.ApplyAutoAssignAsync(db, caller: "boot", ct: default);
    // Startup config: enforce canonical model/quota/evaluator-model on
    // all 8 crews and guarantee each day/night shift has ≥ 2 members.
    var startupLogger = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("ForgeSeed");
    await ForgeSeed.EnsureStartupConfigAsync(db, startupLogger);
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
// ObjectDisposedException on shutdown — swallow instead of crashing.
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    e.SetObserved();
};

app.Run();

/// <summary>
/// Sentinel for <c>WebApplicationFactory&lt;Program&gt;</c> in integration tests.
/// </summary>
public partial class Program { }
