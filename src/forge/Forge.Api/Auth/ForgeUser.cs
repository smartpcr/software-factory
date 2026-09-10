// -----------------------------------------------------------------------
// <copyright file="ForgeUser.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using System.Security.Claims;
using Microsoft.AspNetCore.Http;

/// <summary>Identity comes from ASP.NET authentication; the legacy header has no authority.</summary>
public static class ForgeUser
{
    public const string HeaderName = "X-Forge-User";
    public static string? UserId(HttpContext ctx) => ctx.User.Identity?.IsAuthenticated == true
        ? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
    public static IResult? RequireWriter(HttpContext ctx) => string.IsNullOrWhiteSpace(UserId(ctx))
        ? Results.Problem(title: "Pair browser", detail: "An authenticated, host-enrolled operator session is required.", statusCode: 401)
        : null;
}
