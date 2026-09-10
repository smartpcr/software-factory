// -----------------------------------------------------------------------
// <copyright file="IUserContext.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using Forge.Api.Providers;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Phase 6.11 — read the per-request user identity + optional per-user PAT.
/// Headers:
///   * authenticated principal — stable enrolled user ID.
///   * <c>X-Forge-Ado-Pat</c>  — PAT used for ADO writes on this user's behalf.
///   * <c>X-Forge-GitHub-Pat</c> — PAT used for GitHub writes on this user's behalf.
/// When neither header is set the server PAT (Forge:Ado:Pat / Forge:GitHub:Pat)
/// is used, but only for read-side sync — provider write paths reject anonymous
/// callers via <see cref="ForgeUser.RequireWriter(HttpContext)"/>.
/// </summary>
public interface IUserContext
{
    string? UserId { get; }

    string? AdoPat { get; }

    string? GitHubPat { get; }

    /// <summary>Build a provider call context for the named provider.</summary>
    ProviderCallContext ForProvider(string providerKind);
}

public sealed class HttpUserContext : IUserContext
{
    public const string AdoPatHeader = "X-Forge-Ado-Pat";
    public const string GitHubPatHeader = "X-Forge-GitHub-Pat";
    public const string IdempotencyHeader = "Idempotency-Key";

    private readonly IHttpContextAccessor accessor;

    public HttpUserContext(IHttpContextAccessor accessor) { this.accessor = accessor; }

    private HttpRequest? Req => this.accessor.HttpContext?.Request;

    public string? UserId =>
        this.accessor.HttpContext is { } ctx ? ForgeUser.UserId(ctx) : null;

    public string? AdoPat =>
        this.Req?.Headers[AdoPatHeader].FirstOrDefault();

    public string? GitHubPat =>
        this.Req?.Headers[GitHubPatHeader].FirstOrDefault();

    public string? IdempotencyKey =>
        this.Req?.Headers[IdempotencyHeader].FirstOrDefault();

    public ProviderCallContext ForProvider(string providerKind) => new(
        UserId: this.UserId,
        UserPat: providerKind switch
        {
            "ado" => this.AdoPat,
            "github" => this.GitHubPat,
            _ => null,
        },
        IdempotencyKey: this.IdempotencyKey);
}
