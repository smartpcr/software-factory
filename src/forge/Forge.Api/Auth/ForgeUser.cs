// -----------------------------------------------------------------------
// <copyright file="ForgeUser.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Phase-3 placeholder for the auth that lands in Phase 5.
/// Read-only callers (GET) are anonymous. Write callers (POST/PATCH/DELETE)
/// must present an <c>X-Forge-User</c> header so we can attribute the action
/// in the audit trail and reject anonymous mutations cleanly.
/// </summary>
public static class ForgeUser
{
    public const string HeaderName = "X-Forge-User";

    /// <summary>
    /// Returns 401-style problem when the writer header is missing,
    /// otherwise <c>null</c> to signal the request may proceed.
    /// </summary>
    public static IResult? RequireWriter(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue(HeaderName, out var values) || values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
        {
            return Results.Problem(
                title: "Authentication required",
                detail: $"Provide an {HeaderName} header. Phase-5 introduces real auth.",
                statusCode: 401);
        }
        return null;
    }
}
