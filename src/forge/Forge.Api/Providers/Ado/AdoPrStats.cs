// -----------------------------------------------------------------------
// <copyright file="AdoPrStats.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers.Ado;

using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Forge.Api.Contracts;

/// <summary>
/// Phase 28 — fetches per-PR diff statistics from ADO that the basic
/// <c>/pullrequests</c> list response doesn't include: commit count,
/// changed-file count, iteration ("Updates") count, and the latest
/// iteration's <c>updatedDate</c> (the closest signal ADO exposes for
/// "last activity on the PR").
///
/// ADO REST endpoints used:
///   * <c>/pullRequests/{id}/iterations</c>           — iteration list (count = "Updates")
///   * <c>/pullRequests/{id}/commits</c>              — commit list (count)
///   * <c>/pullRequests/{id}/iterations/{lastId}/changes</c> — change entries on the
///                                                       latest iteration (distinct
///                                                       file paths = changed files)
///
/// The HTTP fetch and the JSON parsing are deliberately split so unit
/// tests can feed canned ADO responses into <see cref="ApplyStats"/>
/// without spinning up a real HTTP stack.
/// </summary>
public static class AdoPrStats
{
    /// <summary>
    /// Pure parse helper. Takes the three ADO JSON envelopes and returns a new
    /// <see cref="Pr"/> with <see cref="Pr.Commits"/>, <see cref="Pr.Files"/>,
    /// <see cref="Pr.Updates"/>, and <see cref="Pr.UpdatedAt"/> filled in from
    /// the responses. Inputs that are <c>default</c> JsonElements are skipped
    /// (so callers can pass only what they fetched successfully).
    /// </summary>
    public static Pr ApplyStats(
        Pr basePr,
        JsonElement iterations,
        JsonElement commits,
        JsonElement changes)
    {
        int updatesCount = basePr.Updates;
        int commitsCount = basePr.Commits;
        int filesCount = basePr.Files;
        string? lastUpdated = basePr.UpdatedAt;

        // -- iterations: array under "value" --
        if (iterations.ValueKind == JsonValueKind.Object &&
            iterations.TryGetProperty("value", out var iterArr) &&
            iterArr.ValueKind == JsonValueKind.Array)
        {
            updatesCount = iterArr.GetArrayLength();

            // Walk every iteration to find the max updatedDate (or createdDate
            // when updatedDate is absent on a row). ADO doesn't guarantee they
            // come back sorted, so we compare each.
            DateTimeOffset? maxAt = null;
            foreach (var iter in iterArr.EnumerateArray())
            {
                var dt = TryDate(iter, "updatedDate") ?? TryDate(iter, "createdDate");
                if (dt is not null && (maxAt is null || dt > maxAt)) maxAt = dt;
            }
            if (maxAt is not null) lastUpdated = maxAt.Value.ToUniversalTime().ToString("o");
        }

        // -- commits: object with "count" + "value" --
        if (commits.ValueKind == JsonValueKind.Object)
        {
            if (commits.TryGetProperty("count", out var ct) && ct.TryGetInt32(out var cn))
            {
                commitsCount = cn;
            }
            else if (commits.TryGetProperty("value", out var cv) && cv.ValueKind == JsonValueKind.Array)
            {
                commitsCount = cv.GetArrayLength();
            }
        }

        // -- changes: { changeEntries: [{ item: { path } }] } --
        if (changes.ValueKind == JsonValueKind.Object &&
            changes.TryGetProperty("changeEntries", out var entries) &&
            entries.ValueKind == JsonValueKind.Array)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.TryGetProperty("item", out var item) &&
                    item.TryGetProperty("path", out var p) &&
                    p.ValueKind == JsonValueKind.String)
                {
                    var s = p.GetString();
                    if (!string.IsNullOrEmpty(s)) paths.Add(s);
                }
            }
            filesCount = paths.Count;
        }

        return basePr with
        {
            Commits = commitsCount,
            Files = filesCount,
            Updates = updatesCount,
            UpdatedAt = lastUpdated,
        };
    }

    private static DateTimeOffset? TryDate(JsonElement obj, string field)
    {
        if (!obj.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(v.GetString(), out var dt) ? dt : null;
    }

    /// <summary>
    /// Fetches the three ADO endpoints and returns a stats-enriched copy of
    /// <paramref name="basePr"/>. Network failures on any endpoint are
    /// swallowed and that slice of the data is left at its base value — this
    /// runs from the sync hot path and must not abort the whole batch over
    /// one transient 5xx.
    /// </summary>
    public static async Task<Pr> EnrichAsync(
        Pr basePr,
        HttpClient client,
        string project,
        string repoName,
        long prId,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        var iterations = await TryFetchAsync(
            client,
            $"{project}/_apis/git/repositories/{Uri.EscapeDataString(repoName)}/pullRequests/{prId}/iterations?api-version=7.0",
            log, ct);

        var commits = await TryFetchAsync(
            client,
            $"{project}/_apis/git/repositories/{Uri.EscapeDataString(repoName)}/pullRequests/{prId}/commits?api-version=7.0",
            log, ct);

        // Pick the last iteration id (= count) so we can fetch its changes.
        // Fall back to "1" so a fresh PR with one push still gets file stats.
        long lastIterId = 1;
        if (iterations.ValueKind == JsonValueKind.Object &&
            iterations.TryGetProperty("value", out var iterArr) &&
            iterArr.ValueKind == JsonValueKind.Array && iterArr.GetArrayLength() > 0)
        {
            lastIterId = iterArr.GetArrayLength();
        }

        var changes = await TryFetchAsync(
            client,
            $"{project}/_apis/git/repositories/{Uri.EscapeDataString(repoName)}/pullRequests/{prId}/iterations/{lastIterId}/changes?$top=1000&api-version=7.0",
            log, ct);

        return ApplyStats(basePr, iterations, commits, changes);
    }

    private static async Task<JsonElement> TryFetchAsync(
        HttpClient client, string relativeUrl, ILogger? log, CancellationToken ct)
    {
        try
        {
            using var resp = await client.GetAsync(relativeUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log?.LogDebug("AdoPrStats: GET {Url} returned {Status}", relativeUrl, (int)resp.StatusCode);
                return default;
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            // Clone so the caller can use the element after the document is disposed.
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "AdoPrStats: GET {Url} threw", relativeUrl);
            return default;
        }
    }
}
