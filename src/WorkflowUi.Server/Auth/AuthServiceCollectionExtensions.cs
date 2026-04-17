// -----------------------------------------------------------------------
// <copyright file="AuthServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Auth;

using System;
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Registers authentication, authorization, and audit services.
/// </summary>
public static class AuthServiceCollectionExtensions
{
    /// <summary>Name of the policy scheme that routes between DevBypass and JwtBearer.</summary>
    internal const string PolicySchemeName = "AutoSelect";

    public static IServiceCollection AddWorkflowUiAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment = false)
    {
        var authSection = configuration.GetSection(AuthOptions.SectionName);
        services.Configure<AuthOptions>(authSection);

        var authOptions = new AuthOptions();
        authSection.Bind(authOptions);

        var useDevBypass = isDevelopment && authOptions.AllowDevBypass;

        var authBuilder = services.AddAuthentication(options =>
        {
            if (useDevBypass)
            {
                // Policy scheme routes loopback → DevBypass, all else → JwtBearer
                options.DefaultScheme = PolicySchemeName;
                options.DefaultAuthenticateScheme = PolicySchemeName;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            }
            else
            {
                options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            }
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

        if (useDevBypass)
        {
            authBuilder.AddPolicyScheme(PolicySchemeName, PolicySchemeName, options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    var remoteIp = context.Connection.RemoteIpAddress;
                    if (remoteIp != null && IPAddress.IsLoopback(remoteIp))
                    {
                        return DevBypassAuthenticationHandler.SchemeName;
                    }

                    return JwtBearerDefaults.AuthenticationScheme;
                };
            });
        }

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
