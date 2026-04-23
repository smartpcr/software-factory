// -----------------------------------------------------------------------
// <copyright file="DevBypassTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Auth;

using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WorkflowUi.Server.Auth;
using Xunit;

public class DevBypassTests
{
    private static DevBypassAuthenticationHandler CreateHandler(
        AuthOptions authOpts,
        IHostEnvironment env,
        DefaultHttpContext ctx)
    {
        var options = new OptionsMonitor<AuthenticationSchemeOptions>(
            new AuthenticationSchemeOptions());
        var loggerFactory = NullLoggerFactory.Instance;
        var encoder = UrlEncoder.Default;
        var authOptions = Options.Create(authOpts);

        var handler = new DevBypassAuthenticationHandler(
            options,
            loggerFactory,
            encoder,
            authOptions,
            env,
            new FakeServer());

        var scheme = new AuthenticationScheme(
            DevBypassAuthenticationHandler.SchemeName,
            null,
            typeof(DevBypassAuthenticationHandler));

        handler.InitializeAsync(scheme, ctx).Wait();
        return handler;
    }

    [Fact]
    public async Task DevBypass_EnvProd_NotAllowed()
    {
        var opts = new AuthOptions { AllowDevBypass = true };
        var env = new FakeHostEnvironment("Production");
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        var handler = CreateHandler(opts, env, ctx);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task DevBypass_EnvDev_AllowFlagFalse_NotAllowed()
    {
        var opts = new AuthOptions { AllowDevBypass = false };
        var env = new FakeHostEnvironment("Development");
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        var handler = CreateHandler(opts, env, ctx);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task DevBypass_EnvDev_AllowFlagTrue_Loopback_Allowed()
    {
        var opts = new AuthOptions { AllowDevBypass = true };
        var env = new FakeHostEnvironment("Development");
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        var handler = CreateHandler(opts, env, ctx);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);
        Assert.True(result.Principal!.Identity!.IsAuthenticated);
    }

    [Fact]
    public async Task DevBypass_EnvDev_AllowFlagTrue_NonLoopback_Denied()
    {
        var opts = new AuthOptions { AllowDevBypass = true };
        var env = new FakeHostEnvironment("Development");
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        var handler = CreateHandler(opts, env, ctx);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task DevBypass_PrincipalHas_EditorAndViewer_Roles()
    {
        var opts = new AuthOptions { AllowDevBypass = true };
        var env = new FakeHostEnvironment("Development");
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        var handler = CreateHandler(opts, env, ctx);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.True(result.Principal!.IsInRole("Editor"));
        Assert.True(result.Principal!.IsInRole("Viewer"));
    }

    [Fact]
    public async Task DevBypass_NullRemoteIp_Denied()
    {
        var opts = new AuthOptions { AllowDevBypass = true };
        var env = new FakeHostEnvironment("Development");
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = null;
        var handler = CreateHandler(opts, env, ctx);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
    }

    private class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName)
        {
            this.EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "TestApp";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private class FakeServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();

        public void Dispose()
        {
        }

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
            => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private class OptionsMonitor<T> : IOptionsMonitor<T>
        where T : class, new()
    {
        private readonly T value;

        public OptionsMonitor(T value)
        {
            this.value = value;
        }

        public T CurrentValue => this.value;

        public T Get(string? name) => this.value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
