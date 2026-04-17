// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using WorkflowUi.Server.Endpoints;
using WorkflowUi.Server.Hosting;
using WorkflowUi.Server.Hubs;
using WorkflowUi.Server.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5226");

// JSON serialization: camelCase + ISO-8601 timestamps
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// DI: register engine services
builder.Services.AddExecutionEngine();

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

// Middleware order: CorrelationId → RequestLogging → ExceptionHandler → endpoints
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RequestLoggingEnricher>();
app.UseProblemDetailsExceptionHandler();

// Endpoints
app.MapHealthEndpoints();
app.MapEngineVersionEndpoints();
app.MapWorkflowValidationEndpoints();
app.MapMapperRoundTripEndpoints();

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
