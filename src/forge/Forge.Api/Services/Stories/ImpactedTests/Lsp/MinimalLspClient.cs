// -----------------------------------------------------------------------
// <copyright file="MinimalLspClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.ImpactedTests.Lsp;

using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Phase 59 Stage S2 — minimal JSON-RPC stdio client for talking to a
/// Language Server Protocol server (rust-analyzer, gopls, pylsp,
/// typescript-language-server). Only implements what
/// <see cref="LspImpactedTestProvider"/> needs: the
/// <c>initialize</c> / <c>initialized</c> handshake, a generic
/// request/notification surface for
/// <c>textDocument/documentSymbol</c> +
/// <c>textDocument/references</c> +
/// <c>textDocument/didOpen</c>, and a clean <c>shutdown</c> + <c>exit</c>
/// sequence on dispose.
///
/// <para><b>Why minimal and not LanguageServer.Client?</b> Same reason
/// the rest of Forge.Api avoids heavyweight SDKs (see Phase 5 / 11.5
/// design notes): a 300-line client we own is easier to debug than a
/// 50k-LOC dep with its own opinions about cancellation, telemetry,
/// and threading. We need 5 request types; we don't need workspace
/// folders, semantic highlights, or progress reporting.</para>
///
/// <para><b>Wire format.</b> LSP wraps every JSON message in a HTTP-
/// header-style framing:</para>
/// <code>
/// Content-Length: 123\r\n
/// \r\n
/// {"jsonrpc":"2.0","id":1,"method":"initialize",...}
/// </code>
///
/// <para>Reading: pump stdout line-by-line until the blank line that
/// terminates headers, parse the <c>Content-Length</c> value, then
/// read exactly that many bytes. Routing: every response carries the
/// request's <c>id</c>; we dispatch to the matching
/// <see cref="TaskCompletionSource{JsonNode}"/>. Server-initiated
/// requests (e.g. <c>window/workDoneProgress/create</c>) are
/// auto-responded with a no-op result so they don't deadlock the
/// server's startup.</para>
///
/// <para><b>Lifecycle.</b> Construct → <see cref="InitializeAsync"/>
/// once → make requests → <see cref="DisposeAsync"/>. Dispose sends
/// <c>shutdown</c> + <c>exit</c> and kills the process tree if the
/// server hangs.</para>
/// </summary>
public sealed class MinimalLspClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Process process;
    private readonly Stream stdout;
    private readonly Stream stdin;
    private readonly Task readerLoop;
    private readonly object writeLock = new();
    private readonly CancellationTokenSource lifecycleCts = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> pending = new();
    private int nextRequestId;
    private bool initialized;
    private bool disposed;

    /// <summary>
    /// Spawns the language server process. The caller invokes
    /// <see cref="InitializeAsync"/> next; nothing on the wire flows
    /// until then. <paramref name="extraArgs"/> is whatever the server
    /// needs (e.g. <c>--stdio</c> for typescript-language-server). The
    /// server's <c>cwd</c> is set to <paramref name="workspaceRoot"/>
    /// so it picks up the right config + lockfiles.
    /// </summary>
    /// <param name="serverProgram">Executable name or absolute path
    /// (e.g. <c>rust-analyzer</c>, <c>gopls</c>, <c>pylsp</c>).</param>
    /// <param name="workspaceRoot">Absolute path to the workspace root.
    /// Used as the server's cwd + as the <c>rootUri</c> /
    /// <c>workspaceFolders</c> in the <c>initialize</c> request.</param>
    /// <param name="extraArgs">Extra command-line args passed verbatim
    /// after the program name.</param>
    public MinimalLspClient(string serverProgram, string workspaceRoot, params string[] extraArgs)
    {
        if (string.IsNullOrWhiteSpace(serverProgram)) throw new ArgumentException("serverProgram required", nameof(serverProgram));
        if (string.IsNullOrWhiteSpace(workspaceRoot)) throw new ArgumentException("workspaceRoot required", nameof(workspaceRoot));

        var psi = new ProcessStartInfo(serverProgram)
        {
            WorkingDirectory = workspaceRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in extraArgs) psi.ArgumentList.Add(arg);

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            throw new LspServerUnavailableException(
                $"Could not start LSP server `{serverProgram}`: {ex.Message}. " +
                "Confirm the server is on PATH (e.g. `where {serverProgram}`) and the workspace root exists.",
                ex);
        }
        if (proc is null)
        {
            throw new LspServerUnavailableException(
                $"Process.Start returned null for `{serverProgram}`. " +
                "The server is likely missing from PATH.");
        }

        this.process = proc;
        this.stdout = proc.StandardOutput.BaseStream;
        this.stdin = proc.StandardInput.BaseStream;
        // Drain stderr so the server doesn't block on a full pipe.
        // We don't surface stderr — most servers log noisy startup
        // diagnostics that the resolver doesn't care about.
        _ = Task.Run(async () =>
        {
            try { await proc.StandardError.BaseStream.CopyToAsync(Stream.Null, this.lifecycleCts.Token); }
            catch { /* swallow */ }
        });
        this.readerLoop = Task.Run(this.PumpReadLoopAsync);
    }

    /// <summary>
    /// LSP <c>initialize</c> handshake. Must be called exactly once
    /// before any other request. Sends a minimal client capabilities
    /// payload — we don't need most of the LSP feature surface; this
    /// is enough for rust-analyzer / gopls / pylsp / tsserver to
    /// boot + answer references queries.
    /// </summary>
    public async Task InitializeAsync(string workspaceRoot, CancellationToken ct = default)
    {
        if (this.initialized) throw new InvalidOperationException("Already initialized");
        var rootUri = ToFileUri(workspaceRoot);
        var initParams = new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["rootUri"] = rootUri,
            ["workspaceFolders"] = new JsonArray
            {
                new JsonObject { ["uri"] = rootUri, ["name"] = Path.GetFileName(workspaceRoot) },
            },
            // Minimal capabilities — request only what we use.
            ["capabilities"] = new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["synchronization"] = new JsonObject
                    {
                        ["dynamicRegistration"] = false,
                        ["didSave"] = false,
                    },
                    ["references"] = new JsonObject { ["dynamicRegistration"] = false },
                    ["documentSymbol"] = new JsonObject { ["dynamicRegistration"] = false, ["hierarchicalDocumentSymbolSupport"] = true },
                },
                ["workspace"] = new JsonObject
                {
                    ["workspaceFolders"] = true,
                },
                ["window"] = new JsonObject
                {
                    ["workDoneProgress"] = false,
                },
            },
        };

        _ = await this.RequestAsync("initialize", initParams, ct).ConfigureAwait(false);
        this.SendNotification("initialized", new JsonObject());
        this.initialized = true;
    }

    /// <summary>
    /// Send a JSON-RPC request and await its response. Throws
    /// <see cref="LspProtocolException"/> on a JSON-RPC error or
    /// when the server crashes mid-request. Caller passes the
    /// LSP method name (<c>"textDocument/references"</c>) + the
    /// params object.
    /// </summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonNode? @params, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        var id = Interlocked.Increment(ref this.nextRequestId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pending[id] = tcs;

        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params is not null) envelope["params"] = @params;

        this.WriteMessage(envelope);

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Send a JSON-RPC notification (no response expected). Used for
    /// <c>initialized</c>, <c>textDocument/didOpen</c>, etc.
    /// </summary>
    public void SendNotification(string method, JsonNode? @params)
    {
        this.ThrowIfDisposed();
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (@params is not null) envelope["params"] = @params;
        this.WriteMessage(envelope);
    }

    // ── Wire helpers ─────────────────────────────────────────────────────

    private void WriteMessage(JsonNode envelope)
    {
        // LSP framing: ASCII Content-Length header + blank line + UTF-8 body.
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOpts);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (this.writeLock)
        {
            this.stdin.Write(header, 0, header.Length);
            this.stdin.Write(body, 0, body.Length);
            this.stdin.Flush();
        }
    }

    private async Task PumpReadLoopAsync()
    {
        var headerBuf = new List<byte>(256);
        var bodyPool = ArrayPool<byte>.Shared;
        try
        {
            while (!this.lifecycleCts.IsCancellationRequested)
            {
                // Read headers until the CRLF CRLF terminator.
                headerBuf.Clear();
                int contentLength = -1;
                while (true)
                {
                    var line = await this.ReadLineCrlfAsync(this.lifecycleCts.Token).ConfigureAwait(false);
                    if (line is null)
                    {
                        // EOF — server exited.
                        this.FailAllPending(new LspProtocolException("LSP server closed stdout unexpectedly"));
                        return;
                    }
                    if (line.Length == 0)
                    {
                        // Blank line → end of headers.
                        break;
                    }
                    // Headers we care about: Content-Length. Others ignored.
                    const string clHeader = "Content-Length:";
                    if (line.StartsWith(clHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        var n = line[clHeader.Length..].Trim();
                        if (int.TryParse(n, out var parsed) && parsed >= 0)
                        {
                            contentLength = parsed;
                        }
                    }
                }

                if (contentLength < 0)
                {
                    this.FailAllPending(new LspProtocolException("LSP server sent a message without Content-Length"));
                    return;
                }

                var body = bodyPool.Rent(contentLength);
                try
                {
                    var read = 0;
                    while (read < contentLength)
                    {
                        var chunk = await this.stdout.ReadAsync(
                            body.AsMemory(read, contentLength - read), this.lifecycleCts.Token).ConfigureAwait(false);
                        if (chunk == 0)
                        {
                            this.FailAllPending(new LspProtocolException("LSP server closed stdout mid-message"));
                            return;
                        }
                        read += chunk;
                    }
                    this.DispatchMessage(body.AsSpan(0, contentLength));
                }
                finally
                {
                    bodyPool.Return(body);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            this.FailAllPending(new LspProtocolException($"LSP reader loop crashed: {ex.Message}", ex));
        }
    }

    private async Task<string?> ReadLineCrlfAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var oneByte = new byte[1];
        var sawCr = false;
        while (true)
        {
            var n = await this.stdout.ReadAsync(oneByte.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return null; // EOF
            var b = oneByte[0];
            if (sawCr && b == (byte)'\n')
            {
                return sb.ToString();
            }
            if (sawCr)
            {
                sb.Append('\r');
                sawCr = false;
            }
            if (b == (byte)'\r')
            {
                sawCr = true;
            }
            else
            {
                sb.Append((char)b);
            }
        }
    }

    private void DispatchMessage(ReadOnlySpan<byte> body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body.ToArray());
        }
        catch
        {
            return;
        }
        if (root is not JsonObject obj) return;

        // Response: has `id` + (`result` or `error`).
        if (obj.TryGetPropertyValue("id", out var idNode) && idNode is JsonValue idVal)
        {
            // id can be int or string per JSON-RPC; we always send int.
            if (!idVal.TryGetValue<int>(out var id))
            {
                // Server-initiated request that sent us a string id —
                // ignore (we don't process server requests except a
                // small allowlist below).
                this.HandleServerRequest(obj);
                return;
            }
            if (!this.pending.TryRemove(id, out var tcs)) return;
            if (obj.TryGetPropertyValue("error", out var errNode) && errNode is JsonObject err)
            {
                var msg = err["message"]?.GetValue<string>() ?? "unknown LSP error";
                tcs.TrySetException(new LspProtocolException($"LSP server error on id {id}: {msg}"));
            }
            else
            {
                tcs.TrySetResult(obj.TryGetPropertyValue("result", out var resNode) ? resNode : null);
            }
            return;
        }

        // No id → notification OR server-initiated request without id.
        // Server-initiated requests would have an id (per spec); if
        // there's a method but no id it's a notification (e.g.
        // `window/logMessage`, `textDocument/publishDiagnostics`). Ignore.
    }

    private void HandleServerRequest(JsonObject obj)
    {
        // Auto-respond with a no-op result to any server-initiated
        // request so the server's init flow doesn't deadlock. The
        // common cases are `window/workDoneProgress/create` (gopls,
        // rust-analyzer) and `client/registerCapability` (most
        // servers). Replying with a no-op result is spec-compliant.
        var id = obj["id"];
        if (id is null) return;
        var resp = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = null,
        };
        try { this.WriteMessage(resp); } catch { /* server may have died */ }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var kv in this.pending)
        {
            kv.Value.TrySetException(ex);
        }
        this.pending.Clear();
    }

    private static string ToFileUri(string absolutePath)
    {
        // LSP requires file:// URIs; on Windows the path becomes
        // file:///C:/foo/bar.
        var normalised = absolutePath.Replace('\\', '/');
        if (!normalised.StartsWith("/", StringComparison.Ordinal))
        {
            normalised = "/" + normalised;
        }
        return "file://" + Uri.EscapeDataString(normalised)
            .Replace("%2F", "/", StringComparison.Ordinal)
            .Replace("%3A", ":", StringComparison.Ordinal);
    }

    private void ThrowIfDisposed()
    {
        if (this.disposed) throw new ObjectDisposedException(nameof(MinimalLspClient));
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed) return;
        this.disposed = true;

        // Best-effort polite shutdown: `shutdown` request, then
        // `exit` notification. The server should self-terminate.
        try
        {
            if (this.initialized && !this.process.HasExited)
            {
                using var shortCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { _ = await this.RequestAsync("shutdown", null, shortCts.Token).ConfigureAwait(false); }
                catch { /* swallow */ }
                try { this.SendNotification("exit", null); } catch { /* swallow */ }
            }
        }
        catch { /* swallow */ }

        // Stop the reader pump.
        try { this.lifecycleCts.Cancel(); } catch { }

        // Give the server up to 1s to exit gracefully, then kill.
        try
        {
            if (!this.process.WaitForExit(TimeSpan.FromSeconds(1)))
            {
                try { this.process.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch { }

        try { await this.readerLoop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        try { this.lifecycleCts.Dispose(); } catch { }
        try { this.process.Dispose(); } catch { }
    }
}

/// <summary>
/// Thrown by <see cref="MinimalLspClient"/> when the LSP server can't
/// be started (binary not on PATH, workspace path invalid). The
/// resolver chain treats this as "language not supported" and falls
/// through to the next provider.
/// </summary>
public sealed class LspServerUnavailableException : Exception
{
    public LspServerUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Thrown on a JSON-RPC protocol-level failure (server crashed,
/// returned an error, sent malformed framing). Distinguished from
/// <see cref="LspServerUnavailableException"/> so the caller can
/// tell "server isn't installed" (skip) from "server died mid-query"
/// (log + skip this run, retry next time).
/// </summary>
public sealed class LspProtocolException : Exception
{
    public LspProtocolException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
