// -----------------------------------------------------------------------
// <copyright file="AuthServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Auth;

using System;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Registers authentication, authorization, and audit services.
/// </summary>
public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowUiAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var authSection = configuration.GetSection(AuthOptions.SectionName);
        services.Configure<AuthOptions>(authSection);

        var authOptions = new AuthOptions();
        authSection.Bind(authOptions);

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.Authority = authOptions.Authority;
            options.Audience = authOptions.Audience;
            options.RequireHttpsMetadata = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = !string.IsNullOrEmpty(authOptions.Authority),
                ValidateAudience = !string.IsNullOrEmpty(authOptions.Audience),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(authOptions.ClockSkewSeconds),
            };

            // Allow SignalR to receive token from query string
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    {
                        context.Token = accessToken;
                    }

                    return Task.CompletedTask;
                },
            };
        })
        .AddScheme<AuthenticationSchemeOptions, DevBypassAuthenticationHandler>(
            DevBypassAuthenticationHandler.SchemeName, _ => { });

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            options.AddPolicy(AuthPolicies.EditorPolicy, policy =>
                policy.RequireRole(AuthPolicies.EditorRole));

            options.AddPolicy(AuthPolicies.ViewerPolicy, policy =>
                policy.RequireRole(AuthPolicies.ViewerRole));
        });

        services.AddSingleton<IAuditLog, InMemoryAuditLog>();

        return services;
    }

    /// <summary>
    /// Logs startup warning if dev bypass is active.
    /// </summary>
    public static void LogDevBypassWarning(
        this IServiceProvider services,
        IConfiguration configuration)
    {
        var authOptions = new AuthOptions();
        configuration.GetSection(AuthOptions.SectionName).Bind(authOptions);

        if (authOptions.AllowDevBypass)
        {
            var logger = services.GetRequiredService<ILogger<AuthOptions>>();
            logger.LogWarning("DEV BYPASS ACTIVE — do not use in prod");
        }
    }
}
