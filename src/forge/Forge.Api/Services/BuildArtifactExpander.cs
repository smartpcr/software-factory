// -----------------------------------------------------------------------
// <copyright file="BuildArtifactExpander.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.IO.Compression;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// forge_implementation.md Phase 27 Q27.5 — lazy artifact downloader +
/// zip-slip safe expander. Used by the failure parser when per-stage tails
/// don't yield a parseable failure (Pass 1.5).
/// <para>
/// Storage path: <c>%LOCALAPPDATA%\Forge\artifacts\{provider}\{buildId}\{artifactName}</c>.
/// Per-artifact size cap: <see cref="SyncConstants.BuildArtifactSizeCapMb"/>.
/// Host disk cap: <see cref="SyncConstants.BuildArtifactStorageCapGb"/> (oldest-first eviction).
/// Retention: <see cref="SyncConstants.BuildArtifactRetentionDays"/>.
/// </para>
/// <para>
/// Security contract: every entry's resolved path must start with the
/// extract root after <see cref="Path.GetFullPath(string)"/> normalisation.
/// One bad entry rejects the entire artifact wholesale (no partial
/// extraction). Path traversal attempts increment the
/// <c>forge.sync.build.artifact.zip_slip_blocked</c> alarm counter.
/// </para>
/// </summary>
public sealed class BuildArtifactExpander
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ForgeMetrics metrics;
    private readonly ILogger<BuildArtifactExpander> logger;
    private readonly HttpClient httpClient;

    public BuildArtifactExpander(
        IServiceScopeFactory scopeFactory,
        ForgeMetrics metrics,
        ILogger<BuildArtifactExpander> logger,
        IHttpClientFactory httpClientFactory)
    {
        this.scopeFactory = scopeFactory;
        this.metrics = metrics;
        this.logger = logger;
        this.httpClient = httpClientFactory.CreateClient(nameof(BuildArtifactExpander));
        this.httpClient.Timeout = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Root for all extracted artifacts. Public so tests can override via
    /// the <c>FORGE_ARTIFACT_ROOT</c> env var.
    /// </summary>
    public static string ResolveArtifactRoot()
    {
        var envOverride = Environment.GetEnvironmentVariable("FORGE_ARTIFACT_ROOT");
        if (!string.IsNullOrEmpty(envOverride)) return envOverride;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Forge", "artifacts");
    }

    /// <summary>
    /// Result of a single extraction attempt.
    /// </summary>
    public sealed record ExtractionResult(
        bool Succeeded,
        long ArtifactRowId,
        string? ExtractedPath,
        IReadOnlyList<string> IndexedFiles,
        string? Error);

    /// <summary>
    /// Download (HEAD-gated by size) and zip-slip-safe extract one artifact.
    /// Side effects: writes <c>build_artifacts</c> + <c>build_artifact_files</c>
    /// rows. Returns a result describing what was indexed; callers fall
    /// through to Pass 2 (blocked) on <c>!Succeeded</c>.
    /// </summary>
    public async Task<ExtractionResult> ExtractAsync(
        long buildId,
        string provider,
        string artifactName,
        string downloadUrl,
        CancellationToken ct)
    {
        // -- HEAD: refuse over-cap before downloading --
        long contentLength = -1;
        try
        {
            using var headReq = new HttpRequestMessage(HttpMethod.Head, downloadUrl);
            using var headResp = await this.httpClient.SendAsync(headReq, ct);
            if (headResp.IsSuccessStatusCode && headResp.Content.Headers.ContentLength.HasValue)
            {
                contentLength = headResp.Content.Headers.ContentLength.Value;
            }
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Artifact HEAD failed (continuing best-effort) for {Url}", downloadUrl);
        }

        var capBytes = (long)SyncConstants.BuildArtifactSizeCapMb * 1024L * 1024L;
        if (contentLength > capBytes)
        {
            this.metrics.BuildArtifactOverCapAdd(contentLength);
            return new ExtractionResult(false, 0, null, Array.Empty<string>(), "artifact-over-cap");
        }

        var artifactRoot = ResolveArtifactRoot();
        var buildDir = Path.Combine(artifactRoot, provider, buildId.ToString());
        var safeName = SanitizeFileSegment(artifactName);
        var zipPath = Path.Combine(buildDir, safeName + ".zip");
        var extractDir = Path.Combine(buildDir, safeName);

        Directory.CreateDirectory(buildDir);

        // -- Download --
        try
        {
            await using (var inStream = await this.httpClient.GetStreamAsync(downloadUrl, ct))
            await using (var outStream = File.Create(zipPath))
            {
                await inStream.CopyToAsync(outStream, ct);
            }
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Artifact download failed for {Url}", downloadUrl);
            this.metrics.BuildArtifactCorruptedAdd();
            try { if (File.Exists(zipPath)) File.Delete(zipPath); }
            catch { /* best effort */ }
            return new ExtractionResult(false, 0, null, Array.Empty<string>(), "download-failed");
        }

        // Re-check on-disk size in case HEAD didn't carry Content-Length.
        var fi = new FileInfo(zipPath);
        if (fi.Length > capBytes)
        {
            this.metrics.BuildArtifactOverCapAdd(fi.Length);
            File.Delete(zipPath);
            return new ExtractionResult(false, 0, null, Array.Empty<string>(), "artifact-over-cap");
        }

        // -- Zip-slip-safe extraction --
        Directory.CreateDirectory(extractDir);
        var extractRoot = Path.GetFullPath(extractDir);
        if (!extractRoot.EndsWith(Path.DirectorySeparatorChar)) extractRoot += Path.DirectorySeparatorChar;
        var indexedFiles = new List<(string RelPath, long SizeBytes, bool IsLog)>();

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.FullName)) continue;
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                    entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                {
                    // Directory entry — skip (created on-demand below).
                    continue;
                }

                // Reject absolute paths and `..` traversal up-front.
                if (Path.IsPathRooted(entry.FullName))
                {
                    this.metrics.BuildArtifactZipSlipBlockedAdd();
                    SafeRemoveDir(extractDir);
                    return new ExtractionResult(false, 0, null, Array.Empty<string>(), "zip-slip-rooted");
                }

                var targetPath = Path.GetFullPath(Path.Combine(extractDir, entry.FullName));
                if (!targetPath.StartsWith(extractRoot, StringComparison.OrdinalIgnoreCase))
                {
                    this.metrics.BuildArtifactZipSlipBlockedAdd();
                    SafeRemoveDir(extractDir);
                    return new ExtractionResult(false, 0, null, Array.Empty<string>(), "zip-slip-traversal");
                }

                // Symlink detection — refuse external link entries on supported runtimes.
                if (IsSymlinkEntry(entry))
                {
                    this.metrics.BuildArtifactZipSlipBlockedAdd();
                    SafeRemoveDir(extractDir);
                    return new ExtractionResult(false, 0, null, Array.Empty<string>(), "zip-slip-symlink");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: true);

                var rel = Path.GetRelativePath(extractDir, targetPath).Replace('\\', '/');
                indexedFiles.Add((rel, entry.Length, IsLogExtension(rel)));
            }
        }
        catch (InvalidDataException idex)
        {
            this.logger.LogWarning(idex, "Artifact corrupt (truncated zip?) at {Path}", zipPath);
            this.metrics.BuildArtifactCorruptedAdd();
            SafeRemoveDir(extractDir);
            try { File.Delete(zipPath); } catch { /* best effort */ }
            return new ExtractionResult(false, 0, null, Array.Empty<string>(), "corrupted-zip");
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Artifact extract failed at {Path}", zipPath);
            SafeRemoveDir(extractDir);
            return new ExtractionResult(false, 0, null, Array.Empty<string>(), "extract-failed");
        }

        // -- Persist artifact + indexed files --
        long artifactRowId;
        var indexed = indexedFiles.Select(x => x.RelPath).ToList();
        using (var scope = this.scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var existing = await db.BuildArtifacts.FirstOrDefaultAsync(
                a => a.BuildId == buildId && a.Name == artifactName, ct);
            if (existing is null)
            {
                existing = new BuildArtifactRow
                {
                    BuildId = buildId,
                    Name = artifactName,
                    DownloadUrl = downloadUrl,
                };
                db.BuildArtifacts.Add(existing);
            }
            existing.SizeBytes = fi.Length;
            existing.ExtractedPath = extractDir;
            existing.FetchedAt = DateTimeOffset.UtcNow.ToString("o");
            existing.ExpiredAt = DateTimeOffset.UtcNow
                .AddDays(SyncConstants.BuildArtifactRetentionDays).ToString("o");
            await db.SaveChangesAsync(ct);
            artifactRowId = existing.Id;

            // Replace any prior file index for this artifact.
            var stale = await db.BuildArtifactFiles.Where(f => f.ArtifactId == artifactRowId).ToListAsync(ct);
            if (stale.Count > 0) db.BuildArtifactFiles.RemoveRange(stale);
            foreach (var (rel, size, isLog) in indexedFiles)
            {
                db.BuildArtifactFiles.Add(new BuildArtifactFileRow
                {
                    ArtifactId = artifactRowId,
                    RelPath = rel,
                    SizeBytes = size,
                    IsLog = isLog,
                });
            }
            await db.SaveChangesAsync(ct);
        }

        this.metrics.BuildArtifactsExtractedAdd();
        return new ExtractionResult(true, artifactRowId, extractDir, indexed, null);
    }

    /// <summary>
    /// Phase 27 — daily janitor entry point. Prunes
    /// <c>build_artifacts</c> older than <see cref="SyncConstants.BuildArtifactRetentionDays"/>
    /// and triggers oldest-first eviction when the host's artifact tree
    /// exceeds <see cref="SyncConstants.BuildArtifactStorageCapGb"/>.
    /// </summary>
    public async Task RunJanitorAsync(CancellationToken ct)
    {
        var artifactRoot = ResolveArtifactRoot();
        if (!Directory.Exists(artifactRoot)) return;

        var threshold = DateTimeOffset.UtcNow.AddDays(-SyncConstants.BuildArtifactRetentionDays).ToString("o");
        using (var scope = this.scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var expired = await db.BuildArtifacts
                .Where(a => a.FetchedAt != null && string.Compare(a.FetchedAt, threshold) < 0)
                .ToListAsync(ct);
            foreach (var row in expired)
            {
                try
                {
                    if (!string.IsNullOrEmpty(row.ExtractedPath) && Directory.Exists(row.ExtractedPath))
                        Directory.Delete(row.ExtractedPath, recursive: true);
                    var zip = (row.ExtractedPath ?? string.Empty) + ".zip";
                    if (File.Exists(zip)) File.Delete(zip);
                }
                catch (Exception ex)
                {
                    this.logger.LogDebug(ex, "Janitor: failed to delete {Path}", row.ExtractedPath);
                }
                var staleFiles = db.BuildArtifactFiles.Where(f => f.ArtifactId == row.Id);
                db.BuildArtifactFiles.RemoveRange(staleFiles);
                db.BuildArtifacts.Remove(row);
            }
            if (expired.Count > 0) await db.SaveChangesAsync(ct);
        }

        // Disk-cap eviction (oldest-first).
        var capBytes = (long)SyncConstants.BuildArtifactStorageCapGb * 1024L * 1024L * 1024L;
        var totalSize = MeasureDirectory(artifactRoot);
        while (totalSize > capBytes)
        {
            using var scope2 = this.scopeFactory.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var oldest = await db2.BuildArtifacts
                .OrderBy(a => a.FetchedAt)
                .FirstOrDefaultAsync(ct);
            if (oldest is null) break;
            try
            {
                if (!string.IsNullOrEmpty(oldest.ExtractedPath) && Directory.Exists(oldest.ExtractedPath))
                    Directory.Delete(oldest.ExtractedPath, recursive: true);
            }
            catch { /* best effort */ }
            var stale = db2.BuildArtifactFiles.Where(f => f.ArtifactId == oldest.Id);
            db2.BuildArtifactFiles.RemoveRange(stale);
            db2.BuildArtifacts.Remove(oldest);
            await db2.SaveChangesAsync(ct);
            this.metrics.BuildArtifactEvictedAdd();
            totalSize = MeasureDirectory(artifactRoot);
        }
    }

    private static bool IsLogExtension(string relPath)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        return ext is ".log" or ".txt" or ".trx" or ".xml";
    }

    private static bool IsSymlinkEntry(ZipArchiveEntry entry)
    {
        // ExternalAttributes carries the upper 16 bits of unix mode on most archivers;
        // S_IFLNK == 0xA000, mode mask == 0xF000. Test permissively to catch symlinks
        // even when the archive was authored by `tar -czf` / `zip --symlinks`.
        const int S_IFMT = 0xF000;
        const int S_IFLNK = 0xA000;
        var mode = (entry.ExternalAttributes >> 16) & S_IFMT;
        return mode == S_IFLNK;
    }

    private static string SanitizeFileSegment(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrEmpty(clean) ? "artifact" : clean;
    }

    private static void SafeRemoveDir(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { /* best effort */ }
    }

    private static long MeasureDirectory(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
        return total;
    }
}
