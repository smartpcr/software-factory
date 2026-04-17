// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using WorkflowUi.Server.Auth;
using WorkflowUi.Server.Endpoints;
using WorkflowUi.Server.Files;
using WorkflowUi.Server.Hosting;
using WorkflowUi.Server.Hubs;
using WorkflowUi.Server.Persistence;
using WorkflowUi.Server.Telemetry;
using WorkflowUi.Server.Version;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5226");

// JSON serialization: camelCase + ISO-8601 timestamps
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// OpenAPI / Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Workflow UI API",
        Version = "v1",
        Description = "REST API for the Workflow UI Server.",
    });
});

// DI: register engine services
builder.Services.AddExecutionEngine();

// DI: register auth services
builder.Services.AddWorkflowUiAuth(builder.Configuration, builder.Environment.IsDevelopment());

// DI: register persistence services
builder.Services.Configure<PersistenceOptions>(
    builder.Configuration.GetSection(PersistenceOptions.SectionName));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IWorkflowStore, FileWorkflowStore>();
builder.Services.AddSingleton<RunHistoryWriter>();
builder.Services.AddSingleton<RunHistoryReader>();
builder.Services.AddSingleton<IdGenerator>();
builder.Services.AddHostedService<RunCleanupHostedService>();

// DI: register telemetry services
builder.Services.AddSingleton<SequenceAllocator>();
builder.Services.AddSingleton<RunHistoryBuffer>();
builder.Services.AddSingleton<EdgeActivationDeriver>();
builder.Services.AddSingleton<SkipDeriver>();
builder.Services.AddSingleton<EngineEventBridge>();
builder.Services.AddSingleton<IRunEventSink, TelemetryHubSink>();

// SignalR
builder.Services.AddSignalR();

// Problem details
builder.AddProblemDetailsServices();

var app = builder.Build();

// Log dev bypass warning
app.Services.LogDevBypassWarning(builder.Configuration);

// Middleware order: VersionHeader → CorrelationId → RequestLogging → ExceptionHandler → Auth → Audit → endpoints
app.UseMiddleware<VersionHeaderMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RequestLoggingEnricher>();
app.UseProblemDetailsExceptionHandler();

// OpenAPI / Swagger (available in all environments for tooling; UI only in dev)
app.UseSwagger();
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AuditMiddleware>();

// Endpoints
app.MapHealthEndpoints();
app.MapEngineVersionEndpoints();
app.MapWorkflowValidationEndpoints();
app.MapMapperRoundTripEndpoints();
app.MapWorkflowEndpoints();
app.MapFilesEndpoints();
app.MapVersionEndpoints();

// SignalR hub
app.MapHub<TelemetryHub>("/hubs/telemetry");

var logger= app.Services.GetRequiredService<ILogger<Program>>();
var versionProvider = app.Services.GetRequiredService<EngineAssemblyVersionProvider>();
logger.LogInformation(
    "WorkflowUi.Server started on {MachineName} in {EnvironmentName} with engine v{EngineAssemblyVersion}",
    Environment.MachineName,
    app.Environment.EnvironmentName,
    versionProvider.GetVersion().EngineAssemblyVersion);

app.Run();

/// <summary>
/// Sentinel class for WebApplicationFactory in integration tests.
/// </summary>
public partial class Program { }
