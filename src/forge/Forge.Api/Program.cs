// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using Forge.Api.Endpoints;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.EntityFrameworkCore;

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

// Problem details (RFC-7807).
builder.Services.AddProblemDetails();

var app = builder.Build();

// Ensure DB exists and is seeded (idempotent).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
    await db.Database.EnsureCreatedAsync();
    await ForgeSeed.EnsureSeededAsync(db);
}

app.UseExceptionHandler();
app.UseStatusCodePages();

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

app.MapHub<ForgeHub>("/hubs/forge");

app.Run();

/// <summary>
/// Sentinel for <c>WebApplicationFactory&lt;Program&gt;</c> in integration tests.
/// </summary>
public partial class Program { }
