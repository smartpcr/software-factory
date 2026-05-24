// -----------------------------------------------------------------------
// <copyright file="LspImpactedTestProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.ImpactedTests.Lsp;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

/// <summary>
/// Phase 59 Stage S2 — LSP-driven impacted-test selection for non-C#
/// languages (Rust / Go / Python / TypeScript). Sits in the
/// <see cref="ImpactedTestResolverChain"/> between
/// <see cref="Roslyn.RoslynImpactedTestProvider"/> (which handles C#)
/// and the heuristic fallback.
///
/// <para><b>Flow per call</b>:</para>
/// <list type="number">
///   <item>Walk the registered <see cref="ILspLanguageSupport"/>
///         instances, ask each <see cref="ILspLanguageSupport.DetectAsync"/>
///         in priority order, take the first match. (Priority is
///         insertion order — Rust first, then Go, Python, TypeScript.)</item>
///   <item>Filter the changed files to the language's source
///         extensions. If none match, return Skipped.</item>
///   <item>Spawn (or reuse) the language server via
///         <see cref="MinimalLspClient"/>. If the server isn't on
///         PATH, return null (chain falls to next provider —
///         typically the heuristic).</item>
///   <item>For each changed source file, send
///         <c>textDocument/didOpen</c> + <c>textDocument/documentSymbol</c>
///         to enumerate the file's top-level symbols.</item>
///   <item>For each symbol, send <c>textDocument/references</c>;
///         for each reference location, load the referenced file
///         (cached) + invoke
///         <see cref="ILspLanguageSupport.IsTestReference"/>.</item>
///   <item>Collect matched
///         <see cref="TestEntryPoint"/>s, rewrite the command via
///         <see cref="ILspLanguageSupport.RewriteCommand"/>, return.</item>
/// </list>
///
/// <para><b>Server caching</b>: a successful spawn is cached per
/// <c>(workspace, languageId)</c>. Subsequent calls reuse the same
/// server process so the cold-start cost (typically 5-15s) amortises
/// across gate runs. Cache evicts on <see cref="Dispose"/> /
/// process exit.</para>
/// </summary>
public sealed class LspImpactedTestProvider : IImpactedTestProvider, IAsyncDisposable
{
    public string Name => "lsp";

    private readonly IReadOnlyList<ILspLanguageSupport> languages;
    private readonly ConcurrentDictionary<string, ServerHandle> serverCache = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    /// <summary>
    /// Source-code extensions that belong to OTHER providers /
    /// languages. When the diff carries a file with one of these
    /// extensions that is NOT in the picked language's
    /// <see cref="ILspLanguageSupport.SourceFileExtensions"/>, this
    /// provider must NOT terminate the chain with a
    /// <see cref="ImpactedTestResolverResult.Skipped"/> result — it can
    /// only speak for its own language. It returns <c>null</c> instead
    /// so the chain falls through to a provider that CAN handle the
    /// foreign source (Roslyn / the C# heuristic for <c>.cs</c>, etc.).
    ///
    /// <para><b>Operator bug 2026-05-20</b>: a C#-only diff (the
    /// <c>UpdateService</c> integration-test migration) reached this
    /// provider after Roslyn's <c>MSBuildWorkspace</c> load failed and
    /// the chain fell through. The repo also carried a
    /// <c>tsconfig.json</c>, so the TypeScript support claimed the
    /// workspace, found zero <c>.ts</c> files in the all-<c>.cs</c>
    /// diff, and returned <c>Skipped</c> — silently skipping the test
    /// gate on a PR that was <i>entirely</i> test changes. Deferring on
    /// foreign source code lets the heuristic catch the changed test
    /// projects so the gate actually runs.</para>
    /// </summary>
    private static readonly IReadOnlyCollection<string> KnownSourceCodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // C# / .NET — handled downstream by Roslyn + the heuristic.
        ".cs", ".fs", ".vb",
        // The LSP-supported languages themselves.
        ".rs", ".go", ".py", ".ts", ".tsx", ".js", ".jsx", ".mts", ".cts",
        // Other common compiled / managed languages a future provider
        // (or the same provider for a different detected language) owns.
        ".java", ".kt", ".kts", ".scala", ".rb", ".php",
        ".c", ".cc", ".cpp", ".cxx", ".h", ".hpp", ".hh",
        ".swift", ".m", ".mm",
    };

    /// <summary>
    /// Default-wired with the four S2 language supports in priority
    /// order (Rust, Go, Python, TypeScript). Tests construct with a
    /// custom set when they want to pin a specific support.
    /// </summary>
    public LspImpactedTestProvider()
        : this(new ILspLanguageSupport[]
        {
            new Languages.RustLanguageSupport(),
            new Languages.GoLanguageSupport(),
            new Languages.PythonLanguageSupport(),
            new Languages.TypeScriptLanguageSupport(),
        })
    {
    }

    public LspImpactedTestProvider(IReadOnlyList<ILspLanguageSupport> languages)
    {
        this.languages = languages ?? throw new ArgumentNullException(nameof(languages));
    }

    public async Task<bool> CanHandleAsync(string workspace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)) return false;
        return await this.PickLanguageAsync(workspace, ct).ConfigureAwait(false) is not null;
    }

    public async Task<ImpactedTestResolverResult?> ResolveAsync(
        string workspace,
        IReadOnlyList<string> changedFiles,
        string originalCommand,
        CancellationToken ct)
    {
        if (this.disposed) return null;

        var language = await this.PickLanguageAsync(workspace, ct).ConfigureAwait(false);
        if (language is null) return null;

        // Does the diff carry source code in a language this provider
        // does NOT own? If so, a Skipped result here would wrongly
        // terminate the chain and skip tests another provider could
        // find — defer (null) instead. We can only speak for our own
        // language's files.
        var diffHasForeignSourceCode = DiffHasForeignSourceCode(changedFiles, language);

        // Filter the changed-files list to the language's source
        // extensions. A diff that touched only non-source files
        // (READMEs, configs) yields Skipped.
        var sourceFiles = changedFiles
            .Where(f => language.SourceFileExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .Select(f => NormaliseToAbsolute(workspace, f))
            .Where(f => f is not null && File.Exists(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourceFiles.Count == 0)
        {
            // No files of THIS language in the diff. If the diff carries
            // source code in another language, defer so a provider that
            // can handle it gets a turn — never skip the gate on a diff
            // we didn't actually examine. Only a purely docs / config
            // diff (no foreign source) is a safe Skipped here.
            if (diffHasForeignSourceCode) return null;
            return new ImpactedTestResolverResult(
                Command: originalCommand,
                Rewritten: false,
                ImpactedProjects: Array.Empty<string>(),
                AllTestProjectCount: 0,
                WholeSuiteFallback: false,
                Skipped: true,
                Reason: $"no {language.LanguageId} source files in the diff",
                Mode: "lsp");
        }

        // Spawn / reuse the server.
        ServerHandle? handle;
        try
        {
            handle = await this.GetOrSpawnServerAsync(workspace, language, ct).ConfigureAwait(false);
        }
        catch (LspServerUnavailableException)
        {
            // Server isn't on PATH. Return null so the chain falls
            // through to the heuristic; the operator's diagnostic
            // header will still mark this as a heuristic fallback.
            return null;
        }
        catch (LspProtocolException ex)
        {
            // Server crashed during startup or initialize.
            Debug.WriteLine($"LspImpactedTestProvider: server protocol error: {ex.Message}");
            return null;
        }

        // Walk references for each changed source file. The
        // protocol is verbose but each request is small; cap by
        // wall-clock cancellation in case a server hangs.
        var matched = new List<TestEntryPoint>();
        var sourceCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var file in sourceFiles!)
            {
                if (file is null) continue;
                await this.PumpReferencesForFileAsync(handle.Client, file!, language, matched, sourceCache, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Server died mid-query — return null so the chain
            // falls through. Don't propagate; the resolver chain
            // expects providers to fail soft.
            Debug.WriteLine($"LspImpactedTestProvider: mid-query error: {ex.Message}");
            return null;
        }

        if (matched.Count == 0)
        {
            // We examined this language's changed files but found no
            // impacted tests. If the diff ALSO carries foreign source
            // code we never examined, defer rather than terminate the
            // chain with a skip — the heuristic / Roslyn may still find
            // impacted tests for the files we don't own.
            if (diffHasForeignSourceCode) return null;
            return new ImpactedTestResolverResult(
                Command: originalCommand,
                Rewritten: false,
                ImpactedProjects: Array.Empty<string>(),
                AllTestProjectCount: 0,
                WholeSuiteFallback: false,
                Skipped: true,
                Reason: $"no impacted {language.LanguageId} tests",
                Mode: "lsp");
        }

        var rewritten = language.RewriteCommand(originalCommand, workspace, matched);
        var owningPaths = matched
            .Select(t => TryRelative(workspace, t.OwningPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ImpactedTestResolverResult(
            Command: rewritten,
            Rewritten: true,
            ImpactedProjects: owningPaths,
            AllTestProjectCount: 0, // LSP path doesn't enumerate a denominator; UI shows the matched set directly
            WholeSuiteFallback: false,
            Skipped: false,
            Reason: $"matched {matched.Count} {language.LanguageId} test(s) via LSP",
            Mode: "lsp");
    }

    // ── Foreign-source detection ────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="changedFiles"/> contains at least one
    /// recognised source-code file whose extension is NOT one of
    /// <paramref name="language"/>'s own
    /// <see cref="ILspLanguageSupport.SourceFileExtensions"/>. Used to
    /// decide whether a would-be Skipped result must be downgraded to a
    /// chain-deferring <c>null</c> (see <see cref="KnownSourceCodeExtensions"/>).
    /// Docs / config-only diffs return false so they still skip cleanly.
    /// </summary>
    private static bool DiffHasForeignSourceCode(IReadOnlyList<string> changedFiles, ILspLanguageSupport language)
    {
        foreach (var f in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            var ext = Path.GetExtension(f);
            if (string.IsNullOrEmpty(ext)) continue;
            // A file in this language's own extensions isn't "foreign".
            if (language.SourceFileExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))) continue;
            if (KnownSourceCodeExtensions.Contains(ext)) return true;
        }
        return false;
    }

    // ── Language selection ──────────────────────────────────────────────

    private async Task<ILspLanguageSupport?> PickLanguageAsync(string workspace, CancellationToken ct)
    {
        // Prefer the shared WorkspaceEcosystemDetector — the SAME detector
        // the qa-e2e generator uses — so the two subsystems can never
        // disagree about a repo's language. It's monorepo-aware + scored, so
        // a Go repo with a stray Cargo.toml is correctly picked as `go`
        // rather than whichever language happens to sit first in priority
        // order (the previous DetectAsync loop returned the first marker hit).
        var ecosystem = WorkspaceEcosystemDetector.Detect(workspace);
        var mapped = WorkspaceEcosystemDetector.ToLspLanguageId(ecosystem);
        if (mapped is not null)
        {
            var match = this.languages.FirstOrDefault(
                l => string.Equals(l.LanguageId, mapped, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        // Fall back to the per-language marker probe — covers tsconfig-only
        // TypeScript repos (the ecosystem detector keys node off package.json),
        // custom language sets injected by tests, and anything the detector
        // reports as `unknown`.
        foreach (var lang in this.languages)
        {
            try
            {
                if (await lang.DetectAsync(workspace, ct).ConfigureAwait(false)) return lang;
            }
            catch
            {
                continue;
            }
        }
        return null;
    }

    // ── Server lifecycle + cache ─────────────────────────────────────────

    private Task<ServerHandle> GetOrSpawnServerAsync(
        string workspace, ILspLanguageSupport language, CancellationToken ct)
    {
        var key = $"{Path.GetFullPath(workspace)}|{language.LanguageId}";
        if (this.serverCache.TryGetValue(key, out var existing) && existing.IsAlive)
        {
            return Task.FromResult(existing);
        }
        // Stale entry — evict + respawn.
        if (existing is not null) this.serverCache.TryRemove(key, out _);
        return this.SpawnServerAsync(workspace, language, key, ct);
    }

    private async Task<ServerHandle> SpawnServerAsync(
        string workspace, ILspLanguageSupport language, string cacheKey, CancellationToken ct)
    {
        var client = new MinimalLspClient(language.ServerProgram, workspace, language.ServerArgs.ToArray());
        try
        {
            await client.InitializeAsync(workspace, ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        var handle = new ServerHandle(client);
        this.serverCache[cacheKey] = handle;
        return handle;
    }

    // ── Per-file references walk ─────────────────────────────────────────

    private async Task PumpReferencesForFileAsync(
        MinimalLspClient client,
        string filePath,
        ILspLanguageSupport language,
        List<TestEntryPoint> sink,
        Dictionary<string, List<string>> sourceCache,
        CancellationToken ct)
    {
        // didOpen so the server registers the file.
        string text;
        try { text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false); }
        catch { return; }

        var fileUri = ToFileUri(filePath);
        client.SendNotification("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = fileUri,
                ["languageId"] = language.LanguageId,
                ["version"] = 1,
                ["text"] = text,
            },
        });

        // documentSymbol — give us every top-level symbol in the file.
        // Some servers (rust-analyzer) need a brief warm-up after didOpen
        // before symbols are ready; for v1 we accept that the first
        // query might return [] on a cold server.
        var symbolsResp = await client.RequestAsync("textDocument/documentSymbol", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = fileUri },
        }, ct).ConfigureAwait(false);

        var symbolLocations = ExtractSymbolLocations(symbolsResp).ToList();
        if (symbolLocations.Count == 0) return;

        foreach (var (line, character) in symbolLocations)
        {
            JsonNode? refsResp;
            try
            {
                refsResp = await client.RequestAsync("textDocument/references", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = fileUri },
                    ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
                    ["context"] = new JsonObject { ["includeDeclaration"] = false },
                }, ct).ConfigureAwait(false);
            }
            catch (LspProtocolException)
            {
                continue;
            }
            if (refsResp is not JsonArray refsArr) continue;

            foreach (var loc in refsArr)
            {
                if (loc is not JsonObject locObj) continue;
                var refUri = locObj["uri"]?.GetValue<string>();
                var range = locObj["range"];
                if (refUri is null || range?["start"] is not JsonObject startObj) continue;
                var refLine = startObj["line"]?.GetValue<int>() ?? 0;
                var refChar = startObj["character"]?.GetValue<int>() ?? 0;
                var refPath = FromFileUri(refUri);
                if (refPath is null) continue;

                var refSrc = LoadSource(refPath, sourceCache);
                if (refSrc is null) continue;

                var tp = language.IsTestReference(refPath, refSrc, refLine, refChar);
                if (tp is not null) sink.Add(tp);
            }
        }
    }

    private static IEnumerable<(int Line, int Char)> ExtractSymbolLocations(JsonNode? symbolsResp)
    {
        if (symbolsResp is not JsonArray arr) yield break;
        foreach (var item in arr)
        {
            // DocumentSymbol (hierarchical) has `.range.start`;
            // SymbolInformation (flat) has `.location.range.start`.
            if (item is not JsonObject obj) continue;
            JsonNode? start = obj["range"]?["start"]
                ?? obj["location"]?["range"]?["start"]
                ?? obj["selectionRange"]?["start"];
            if (start is JsonObject startObj)
            {
                var line = startObj["line"]?.GetValue<int>() ?? 0;
                var ch = startObj["character"]?.GetValue<int>() ?? 0;
                yield return (line, ch);
            }
            // Recurse into children for hierarchical symbols.
            if (obj["children"] is JsonArray children)
            {
                foreach (var sub in ExtractSymbolLocations(children))
                {
                    yield return sub;
                }
            }
        }
    }

    private static List<string>? LoadSource(string path, Dictionary<string, List<string>> cache)
    {
        if (cache.TryGetValue(path, out var existing)) return existing;
        try
        {
            var lines = File.ReadAllLines(path).ToList();
            cache[path] = lines;
            return lines;
        }
        catch
        {
            return null;
        }
    }

    // ── URI / path helpers ──────────────────────────────────────────────

    private static string? NormaliseToAbsolute(string workspace, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var normalised = raw.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.IsPathRooted(normalised)
                ? Path.GetFullPath(normalised)
                : Path.GetFullPath(Path.Combine(workspace, normalised));
        }
        catch
        {
            return null;
        }
    }

    private static string ToFileUri(string absolutePath)
    {
        var normalised = absolutePath.Replace('\\', '/');
        if (!normalised.StartsWith("/", StringComparison.Ordinal)) normalised = "/" + normalised;
        return "file://" + Uri.EscapeDataString(normalised)
            .Replace("%2F", "/", StringComparison.Ordinal)
            .Replace("%3A", ":", StringComparison.Ordinal);
    }

    private static string? FromFileUri(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = Uri.UnescapeDataString(uri["file://".Length..]);
        // On Windows the URI shape is `file:///C:/foo` → strip the
        // leading `/` so `C:/foo` round-trips to a real path.
        if (rest.Length >= 3 && rest[0] == '/' && rest[2] == ':')
        {
            rest = rest[1..];
        }
        return rest.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string TryRelative(string workspace, string filePath)
    {
        try
        {
            return Path.GetRelativePath(workspace, filePath).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch
        {
            return filePath.Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    // ── Dispose ─────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (this.disposed) return;
        this.disposed = true;
        foreach (var handle in this.serverCache.Values)
        {
            try { await handle.Client.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        this.serverCache.Clear();
    }

    private sealed class ServerHandle
    {
        public MinimalLspClient Client { get; }
        public bool IsAlive => true; // Future: probe Client.IsAlive after a process-exit field is added.

        public ServerHandle(MinimalLspClient client)
        {
            this.Client = client;
        }
    }
}
