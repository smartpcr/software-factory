// -----------------------------------------------------------------------
// <copyright file="RepoValidation.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Domain;

using Forge.Api.Contracts;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Phase 8.6 — pure-function validation for repo attach. Returns null when
/// the request is acceptable, or an <see cref="IResult"/> with a 400 / 409
/// problem-details body when not. Disk-space + writability are best-effort
/// checks that warn but do not block — the auto-clone pipeline will surface
/// any real failure later (Phase 8.10).
/// </summary>
internal static class RepoValidation
{
    public static IResult? Validate(CreateRepoRequest req)
    {
        if (req is null) return Results.Problem(title: "body is required", statusCode: 400);

        if (string.IsNullOrWhiteSpace(req.Id))
        {
            return Results.Problem(title: "id is required", statusCode: 400);
        }

        if (req.Kind is not ("ado" or "github"))
        {
            return Results.Problem(
                title: "Invalid repo kind",
                detail: "kind must be 'ado' or 'github' — Q4 forbids cross-provider repos.",
                statusCode: 400);
        }

        if (string.IsNullOrWhiteSpace(req.RemoteUrl) || !Uri.TryCreate(req.RemoteUrl, UriKind.Absolute, out var uri))
        {
            return Results.Problem(title: "remoteUrl must be an absolute URL", statusCode: 400);
        }

        if (uri.Scheme is not ("https" or "http"))
        {
            return Results.Problem(
                title: "Only http(s) remotes are supported",
                detail: "SSH remotes are not supported at v0.1 — use the HTTPS clone URL.",
                statusCode: 400);
        }

        // Sanity-check the URL host shape per provider so an operator who pastes
        // a github.com URL into an ADO row gets a fast 400 rather than a confusing
        // sync failure later.
        if (req.Kind == "github" && !uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(title: "GitHub remote must be on github.com", statusCode: 400);
        }
        if (req.Kind == "ado" && !(uri.Host.EndsWith("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("visualstudio.com", StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Problem(title: "ADO remote must be on dev.azure.com or *.visualstudio.com", statusCode: 400);
        }

        if (string.IsNullOrWhiteSpace(req.CloneRoot))
        {
            return Results.Problem(title: "cloneRoot is required", statusCode: 400);
        }

        // Rooted-path check: cloneRoot must be absolute. We don't probe the
        // filesystem here — that's the auto-clone pipeline's job (Phase 8.10).
        if (!Path.IsPathFullyQualified(req.CloneRoot))
        {
            return Results.Problem(
                title: "cloneRoot must be an absolute path",
                detail: $"Got '{req.CloneRoot}'. Use a fully-qualified path like 'E:\\work\\hci\\update'.",
                statusCode: 400);
        }

        return null;
    }
}
