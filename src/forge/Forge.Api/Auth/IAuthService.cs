// -----------------------------------------------------------------------
// <copyright file="IAuthService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

/// <summary>
/// Phase 9 — interactive sign-in surface. Each provider has its own
/// implementation; sign-in to one does not imply sign-in to the other.
/// Tokens are persisted via MSAL's DPAPI-protected cache so they survive
/// restarts and silent-token acquisition works for the cached account.
/// </summary>
public interface IAuthService
{
    /// <summary>"ado" or "github" — which provider this auth service handles.</summary>
    string Kind { get; }

    /// <summary>True when an account is cached and a fresh access token is obtainable silently.</summary>
    Task<AuthStatus> StatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Triggers an interactive sign-in. WAM broker first on Windows; system
    /// browser auth-code + PKCE elsewhere. Device-code flow is banned (Q7).
    /// Returns the signed-in account on success.
    /// </summary>
    Task<AuthStatus> SignInAsync(CancellationToken ct = default);

    /// <summary>Removes the cached account / refresh token.</summary>
    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a fresh access token (silent if possible, prompting only if
    /// the refresh token expired). Returns null when no account is signed in.
    /// </summary>
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);
}

/// <summary>Snapshot of the current sign-in state, surfaced on /api/auth/{kind}/status.</summary>
public sealed record AuthStatus(
    string Kind,
    bool Connected,
    string? Account,
    string? UserName,
    DateTimeOffset? ExpiresAt,
    string? Error = null);
