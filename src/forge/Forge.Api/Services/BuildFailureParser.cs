// -----------------------------------------------------------------------
// <copyright file="BuildFailureParser.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text.RegularExpressions;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// forge_implementation.md Phase 27 Q27.6 — failure parser.
/// <para>
/// Pass 1: regex pass over the per-stage log tail using
/// <see cref="SyncConstants.BuildFailurePatterns"/>. Each match yields one
/// <see cref="ParsedFailurePoint"/>; multi-match logs produce multiple rows.
/// </para>
/// <para>
/// Pass 1.5: when Pass 1 returns zero matches AND the artifact list contains
/// log-shaped files (.log / .txt / .trx / .xml), the parser unzips on demand
/// and re-runs Pass 1 against each indexed log. Matches are tagged with a
/// sentinel <c>SourceLogTailOffset = -1</c> and the artifact metadata is
/// persisted in the <c>PayloadJson</c> column so the UI can deep-link.
/// </para>
/// <para>
/// Pass 2: when both passes return empty, the runner emits
/// <c>forge.sync.build.unparseable</c> and routes the queue row to
/// <c>state='blocked'</c> — handled by the caller, not this class.
/// </para>
/// </summary>
public sealed class BuildFailureParser
{
    private readonly ForgeMetrics metrics;
    private readonly ILogger<BuildFailureParser> logger;

    /// <summary>Compiled regexes (built once at construction).</summary>
    private readonly Regex[] compiledPatterns;

    public BuildFailureParser(ForgeMetrics metrics, ILogger<BuildFailureParser> logger)
    {
        this.metrics = metrics;
        this.logger = logger;
        this.compiledPatterns = SyncConstants.BuildFailurePatterns
            .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline))
            .ToArray();
    }

    /// <summary>One parsed failure point. Mirrors the row contract.</summary>
    public sealed record ParsedFailurePoint(
        string Kind,
        string? FilePath,
        int? Line,
        int? Column,
        string Message,
        int SourceLogTailOffset,
        string? Severity,
        string? PayloadJson);

    /// <summary>
    /// Run Pass 1 against <paramref name="logTail"/> and Pass 1.5 against
    /// <paramref name="artifactFiles"/> when Pass 1 returns empty. Persists
    /// the parsed rows into <c>build_failure_points</c> against
    /// <paramref name="stageId"/>.
    /// </summary>
    public async Task<IReadOnlyList<ParsedFailurePoint>> ParseAsync(
        long stageId,
        string logTail,
        IReadOnlyList<(string Path, string Content)> artifactFiles,
        ControlDbContext control,
        ProductDbContext db,
        CancellationToken ct)
    {
        var p1 = this.RunPass1(logTail, sourceOffsetBase: 0);

        // Persist Pass 1 hits.
        if (p1.Count > 0)
        {
            await this.PersistAsync(stageId, p1, control, db, ct);
            return p1;
        }

        if (artifactFiles.Count == 0)
        {
            return Array.Empty<ParsedFailurePoint>();
        }

        // -- Pass 1.5: descend into artifact log files --
        var found = new List<ParsedFailurePoint>();
        foreach (var (path, content) in artifactFiles)
        {
            var inner = this.RunPass1(content, sourceOffsetBase: -1);
            foreach (var pt in inner)
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new { artifactRelPath = path });
                found.Add(pt with { PayloadJson = payload, SourceLogTailOffset = -1 });
            }
        }

        if (found.Count > 0)
        {
            await this.PersistAsync(stageId, found, control, db, ct);
        }
        return found;
    }

    /// <summary>
    /// Stateless Pass 1 — runs every locked pattern against the input and
    /// returns one <see cref="ParsedFailurePoint"/> per match.
    /// <paramref name="sourceOffsetBase"/> is added to the regex match index
    /// so callers can carry an artifact sentinel through.
    /// </summary>
    public IReadOnlyList<ParsedFailurePoint> RunPass1(string logContent, int sourceOffsetBase)
    {
        var found = new List<ParsedFailurePoint>();
        if (string.IsNullOrEmpty(logContent)) return found;

        // Pattern indices (matching the SyncConstants.BuildFailurePatterns order):
        //   0 = .NET CSxxxx, 1 = cmake/make FAILED, 2 = generic Error...at, 3 = test assert, 4 = npm
        for (int i = 0; i < this.compiledPatterns.Length; i++)
        {
            var rx = this.compiledPatterns[i];
            var matches = rx.Matches(logContent);
            foreach (Match m in matches)
            {
                if (!m.Success) continue;
                ParsedFailurePoint? pt = i switch
                {
                    0 => Parse_DotNet(m, sourceOffsetBase),
                    1 => Parse_CmakeMake(m, sourceOffsetBase),
                    2 => Parse_GenericStack(m, sourceOffsetBase),
                    3 => Parse_TestAssert(m, sourceOffsetBase),
                    4 => Parse_Npm(m, sourceOffsetBase),
                    _ => null,
                };
                if (pt is not null) found.Add(pt);
                this.metrics.BuildFailuresParsedAdd(
                    pass: sourceOffsetBase < 0 ? "1.5" : "1",
                    kind: pt?.Kind ?? "other");
            }
        }
        return found;
    }

    private async Task PersistAsync(
        long stageId,
        IReadOnlyList<ParsedFailurePoint> rows,
        ControlDbContext control,
        ProductDbContext db,
        CancellationToken ct)
    {
        // Replace previous parse results for this stage on re-run (idempotent on re-fetch).
        var stale = await db.BuildFailurePoints.Where(f => f.StageId == stageId).ToListAsync(ct);
        if (stale.Count > 0) db.BuildFailurePoints.RemoveRange(stale);
        foreach (var pt in rows)
        {
            db.BuildFailurePoints.Add(new BuildFailurePointRow
            {
                StageId = stageId,
                Kind = pt.Kind,
                FilePath = pt.FilePath,
                Line = pt.Line,
                Column = pt.Column,
                Message = pt.Message,
                SourceLogTailOffset = pt.SourceLogTailOffset,
                Severity = pt.Severity,
                PayloadJson = pt.PayloadJson,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    // -- Per-pattern shapers --

    private static ParsedFailurePoint Parse_DotNet(Match m, int baseOffset)
    {
        // Groups: 1 = message, 2 = file, 3 = line.
        int.TryParse(m.Groups[3].Value, out var line);
        return new ParsedFailurePoint(
            Kind: "compile",
            FilePath: m.Groups[2].Value,
            Line: line == 0 ? null : line,
            Column: null,
            Message: m.Groups[1].Value.Trim(),
            SourceLogTailOffset: baseOffset < 0 ? -1 : baseOffset + m.Index,
            Severity: "error",
            PayloadJson: null);
    }

    private static ParsedFailurePoint Parse_CmakeMake(Match m, int baseOffset)
    {
        return new ParsedFailurePoint(
            Kind: "build",
            FilePath: m.Groups[1].Value.Trim(),
            Line: null,
            Column: null,
            Message: m.Value,
            SourceLogTailOffset: baseOffset < 0 ? -1 : baseOffset + m.Index,
            Severity: "error",
            PayloadJson: null);
    }

    private static ParsedFailurePoint Parse_GenericStack(Match m, int baseOffset)
    {
        // Groups: 1 = message, 2 = file, 3 = line.
        int.TryParse(m.Groups[3].Value, out var line);
        return new ParsedFailurePoint(
            Kind: "runtime",
            FilePath: m.Groups[2].Value.Trim(),
            Line: line == 0 ? null : line,
            Column: null,
            Message: m.Groups[1].Value.Trim(),
            SourceLogTailOffset: baseOffset < 0 ? -1 : baseOffset + m.Index,
            Severity: "error",
            PayloadJson: null);
    }

    private static ParsedFailurePoint Parse_TestAssert(Match m, int baseOffset)
    {
        // Groups: 1 = file, 2 = line.
        int.TryParse(m.Groups[2].Value, out var line);
        return new ParsedFailurePoint(
            Kind: "test",
            FilePath: m.Groups[1].Value.Trim(),
            Line: line == 0 ? null : line,
            Column: null,
            Message: m.Value,
            SourceLogTailOffset: baseOffset < 0 ? -1 : baseOffset + m.Index,
            Severity: "error",
            PayloadJson: null);
    }

    private static ParsedFailurePoint Parse_Npm(Match m, int baseOffset)
    {
        // Group 1 = code (e.g. ELIFECYCLE).
        return new ParsedFailurePoint(
            Kind: "npm",
            FilePath: null,
            Line: null,
            Column: null,
            Message: m.Groups[1].Value.Trim(),
            SourceLogTailOffset: baseOffset < 0 ? -1 : baseOffset + m.Index,
            Severity: "error",
            PayloadJson: null);
    }
}
