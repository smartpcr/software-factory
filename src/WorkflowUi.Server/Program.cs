// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var engineAssemblyVersion = typeof(ExecutionEngine.Workflow.WorkflowDefinition).Assembly
    .GetName().Version?.ToString() ?? "unknown";
logger.LogInformation(
    "WorkflowUi.Server started on {MachineName} in {EnvironmentName} with engine v{EngineAssemblyVersion}",
    Environment.MachineName,
    app.Environment.EnvironmentName,
    engineAssemblyVersion);

app.Run();

/// <summary>
/// Sentinel class for WebApplicationFactory in integration tests.
/// </summary>
public partial class Program { }
