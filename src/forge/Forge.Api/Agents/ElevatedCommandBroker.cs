// -----------------------------------------------------------------------
// <copyright file="ElevatedCommandBroker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Forge.Api.Services;
using Forge.Api.Services.Stories;

/// <summary>
/// Privilege-split test runner. Forge.Api (the web host) runs NON-elevated so
/// every git / agent (Copilot CLI/SDK) / normal-build child is non-elevated —
/// which avoids the whole class of elevation bugs (shared <c>~/.gitconfig</c>
/// access, MSBuildLocator env pollution, worktree pollution, etc.). The ONLY
/// thing that needs Administrator is the build/test command of a repo flagged
/// <see cref="Persistence.RepoRow.RunTestsElevated"/> (its tests write HKLM /
/// bind HttpListener URLs). That single command is marshalled to this broker —
/// a second mode of <c>Forge.Api.exe</c> (<c>--elevated-broker</c>) that the
/// launcher starts ELEVATED once — over a per-user named pipe.
///
/// <para><b>Security model.</b> Both the broker (elevated/high-IL) and
/// Forge.Api (non-elevated/medium-IL) run as the SAME Windows account. The
/// pipe's DACL grants ONLY that user's SID (no other account can submit work),
/// and its mandatory integrity label is lowered to Medium so the non-elevated
/// client can connect to the elevated server. The trust hop is therefore
/// medium→high WITHIN one user account — not a cross-user escalation. The
/// broker runs only the repo's own configured build/test command/script — no
/// arbitrary operator input beyond that.</para>
///
/// <para>Two operations: <c>build</c> (run
/// <see cref="StoryBuildRunner.RunBuildAsync"/> → parsed
/// <see cref="StoryBuildResult"/>, for the story test gate) and <c>script</c>
/// (run a raw cmd script → exit/output, for <see cref="BuildValidationWorker"/>'s
/// repo validation).</para>
/// </summary>
public static class ElevatedCommandBroker
{
    /// <summary>The command-line switch that puts <c>Forge.Api.exe</c> into broker mode.</summary>
    public const string ModeArg = "--elevated-broker";

    /// <summary>Switch that tells a running broker to shut down (used by -Stop).</summary>
    public const string ShutdownArg = "--broker-shutdown";

    private const string OpBuild = "build";
    private const string OpScript = "script";
    private const string OpShutdown = "shutdown";

    // Set by RunServerAsync so a "shutdown" pipe op can stop the accept loop.
    private static CancellationTokenSource? shutdownSource;

    // Test seam: when non-null, overrides the per-user pipe name. Integration
    // tests set a unique pipe so the broker+client round-trip is isolated from
    // a REAL (possibly elevated) broker on the production per-user pipe — a test
    // must never connect to, or send shutdown to, the live broker. Null in
    // production. Broker tests run sequentially (one xUnit class) and reset this
    // in a finally; that matches the single-broker-per-process reality already
    // implied by the static <see cref="shutdownSource"/>.
    internal static string? PipeNameOverride;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Per-user pipe name. Includes the user SID so two accounts on the same
    /// box never share a broker, and so the DACL can be pinned to that SID.
    /// </summary>
    public static string PipeName()
    {
        if (PipeNameOverride is { } o) return o;
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
        return $"forge.elevated.{sid}";
    }

    // ── Broker (server) side — runs ELEVATED ────────────────────────────────

    /// <summary>
    /// Run the broker server loop until <paramref name="ct"/> is cancelled.
    /// Entry point for <c>Forge.Api.exe --elevated-broker</c>.
    /// </summary>
    public static async Task RunServerAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("[forge-broker] elevated broker is Windows-only; exiting.");
            return;
        }

        // Enable SeSecurityPrivilege so we can stamp the pipe's mandatory
        // integrity label (SACL) down to Medium — without it
        // NamedPipeServerStreamAcl.Create throws "A required privilege is not
        // held by the client" and no pipe is ever created. An elevated admin
        // token HAS this privilege but it's disabled by default; enable it now.
        var labelOk = TryEnableSecurityPrivilege();

        // Linked cts so a "shutdown" pipe op (sent by run-forge-ui.ps1 -Stop,
        // non-elevated, no UAC) can stop the accept loop in addition to the
        // caller's ct (--watch-pid host-death / Ctrl-C / ProcessExit).
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        shutdownSource = loopCts;
        var loopCt = loopCts.Token;

        var pipe = PipeName();
        Console.WriteLine($"[forge-broker] elevated test broker on \\\\.\\pipe\\{pipe} (PID {Environment.ProcessId}); integrity-label={(labelOk ? "Medium" : "default")}.");

        while (!loopCt.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServerStream(pipe);
                await server.WaitForConnectionAsync(loopCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[forge-broker] accept failed: {ex.Message}");
                server?.Dispose();
                try { await Task.Delay(500, loopCt).ConfigureAwait(false); } catch { break; }
                continue;
            }

            var accepted = server;
            _ = Task.Run(() => HandleConnectionAsync(accepted, loopCt), loopCt);
        }

        Console.WriteLine("[forge-broker] shutting down.");
    }

    private static NamedPipeServerStream CreateServerStream(string pipe)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot resolve current-user SID for broker pipe ACL.");

        // Preferred: DACL grants ONLY the current user, and the mandatory
        // integrity label (SACL) is lowered to Medium so the non-elevated
        // (medium-IL) client can connect to this elevated (high-IL) server.
        // Requires SeSecurityPrivilege (enabled in RunServerAsync). If applying
        // the labelled descriptor fails for any reason, fall back to a
        // DACL-only pipe: a medium client then can't connect, which the caller
        // treats as "broker unavailable" → workstream stalls (safe, not a crash).
        try
        {
            var labelled = new PipeSecurity();
            labelled.SetSecurityDescriptorSddlForm(
                $"D:(A;;FA;;;{user.Value})S:(ML;;NW;;;ME)",
                AccessControlSections.Access | AccessControlSections.Audit);
            return NamedPipeServerStreamAcl.Create(
                pipe, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 0, labelled);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[forge-broker] labelled pipe create failed ({ex.Message}); falling back to DACL-only "
                + "(non-elevated clients may not connect → elevated test gates will stall).");
            var dacl = new PipeSecurity();
            dacl.AddAccessRule(new PipeAccessRule(
                user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(
                pipe, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 0, dacl);
        }
    }

    // ── SeSecurityPrivilege enable (needed to stamp the pipe's SACL label) ──

    private const int SE_PRIVILEGE_ENABLED = 0x00000002;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES { public LUID Luid; public int Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public int PrivilegeCount; public LUID_AND_ATTRIBUTES Privilege; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Enable <c>SeSecurityPrivilege</c> in the current (elevated) process so
    /// the broker can stamp the pipe's mandatory integrity label. Returns false
    /// (best-effort) if the privilege isn't available — the caller's pipe-create
    /// fallback then runs DACL-only and the medium client can't connect.
    /// </summary>
    private static bool TryEnableSecurityPrivilege()
    {
        var token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
            {
                return false;
            }
            if (!LookupPrivilegeValue(null, "SeSecurityPrivilege", out var luid))
            {
                return false;
            }
            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privilege = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED },
            };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            {
                return false;
            }
            // AdjustTokenPrivileges returns true even if not all privileges were
            // assigned; ERROR_NOT_ALL_ASSIGNED (1300) means the privilege wasn't
            // in the token at all (e.g. running non-elevated).
            return Marshal.GetLastWin32Error() == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using (server)
        {
            string responseJson;
            try
            {
                var requestJson = await ReadFrameAsync(server, ct).ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize<ElevatedRequest>(requestJson, Json)
                    ?? throw new InvalidOperationException("null request envelope");

                if (string.Equals(envelope.Op, OpShutdown, StringComparison.Ordinal))
                {
                    // Graceful stop requested by `-Stop`. Ack, then cancel the
                    // accept loop so the broker process exits — no elevated
                    // taskkill / UAC needed to tear down the elevated broker.
                    responseJson = JsonSerializer.Serialize(new { ok = true }, Json);
                    try { await WriteFrameAsync(server, responseJson, ct).ConfigureAwait(false); } catch { }
                    shutdownSource?.Cancel();
                    return;
                }
                else if (string.Equals(envelope.Op, OpScript, StringComparison.Ordinal) && envelope.Script is { } s)
                {
                    var (code, stdout, stderr, timedOut) = await BuildValidationWorker.RunScriptAsync(
                        s.Script, s.Cwd, s.TimeoutSeconds, s.LogPath).ConfigureAwait(false);
                    responseJson = JsonSerializer.Serialize(
                        new ElevatedScriptResponse(code, stdout, stderr, timedOut, Error: null), Json);
                }
                else if (envelope.Build is { } b)
                {
                    var result = await StoryBuildRunner.RunBuildAsync(
                        workspace: b.Workspace,
                        ct: ct,
                        timeout: b.TimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                        buildCommand: b.BuildCommand,
                        logFilePath: b.LogFilePath,
                        logRelPath: b.LogRelPath,
                        sessionSetupCommands: b.SessionSetupCommands,
                        collectTestFailures: b.CollectTestFailures,
                        trxScopeTag: b.TrxScopeTag).ConfigureAwait(false);
                    responseJson = JsonSerializer.Serialize(new ElevatedBuildResponse(result, Error: null), Json);
                }
                else
                {
                    responseJson = JsonSerializer.Serialize(new { error = "unknown op or missing payload" }, Json);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Both response shapes carry an `Error` field, so a bare
                // {error} object deserializes correctly on either client path.
                responseJson = JsonSerializer.Serialize(new { error = $"{ex.GetType().Name}: {ex.Message}" }, Json);
            }

            try
            {
                await WriteFrameAsync(server, responseJson, ct).ConfigureAwait(false);
            }
            catch { /* client gone — nothing to do */ }
        }
    }

    // ── Client side — runs NON-elevated (the Forge.Api web host) ────────────

    /// <summary>True when an elevated broker is reachable.</summary>
    public static async Task<bool> IsAvailableAsync(CancellationToken ct, int connectTimeoutMs = 1500)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(connectTimeoutMs, ct).ConfigureAwait(false);
            return client.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort graceful stop: tell a running broker to exit (used by
    /// <c>run-forge-ui.ps1 -Stop</c> via <c>Forge.Api.exe --broker-shutdown</c>,
    /// non-elevated, no UAC). Silently no-ops if no broker is listening.
    /// </summary>
    public static async Task RequestShutdownAsync(CancellationToken ct, int connectTimeoutMs = 2000)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(connectTimeoutMs, ct).ConfigureAwait(false);
            await WriteFrameAsync(client, JsonSerializer.Serialize(new ElevatedRequest(OpShutdown, null, null), Json), ct).ConfigureAwait(false);
            try { await ReadFrameAsync(client, ct).ConfigureAwait(false); } catch { /* ack optional */ }
        }
        catch
        {
            // No broker running, or it's already gone — nothing to clean up.
        }
    }

    /// <summary>
    /// Marshal a build/test command to the elevated broker (story test gate)
    /// and return the parsed <see cref="StoryBuildResult"/>. Throws
    /// <see cref="ElevatedBrokerUnavailableException"/> when the broker can't be
    /// reached or errors.
    /// </summary>
    public static async Task<StoryBuildResult> RunBuildAsync(
        ElevatedBuildRequest request, CancellationToken ct, int connectTimeoutMs = 5000)
    {
        var responseJson = await RoundTripAsync(
            new ElevatedRequest(OpBuild, request, null), ct, connectTimeoutMs).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<ElevatedBuildResponse>(responseJson, Json);
        if (response?.Result is { } result)
        {
            return result;
        }
        throw new ElevatedBrokerUnavailableException(
            $"Elevated broker returned an error: {response?.Error ?? "empty response"}");
    }

    /// <summary>
    /// Marshal a raw cmd script to the elevated broker (repo validation) and
    /// return its exit code + captured output. Throws
    /// <see cref="ElevatedBrokerUnavailableException"/> when unreachable/errored.
    /// </summary>
    public static async Task<(int Code, string Stdout, string Stderr, bool TimedOut)> RunScriptAsync(
        ElevatedScriptRequest request, CancellationToken ct, int connectTimeoutMs = 5000)
    {
        var responseJson = await RoundTripAsync(
            new ElevatedRequest(OpScript, null, request), ct, connectTimeoutMs).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<ElevatedScriptResponse>(responseJson, Json);
        if (response is not null && string.IsNullOrEmpty(response.Error))
        {
            return (response.Code, response.Stdout ?? string.Empty, response.Stderr ?? string.Empty, response.TimedOut);
        }
        throw new ElevatedBrokerUnavailableException(
            $"Elevated broker returned an error: {response?.Error ?? "empty response"}");
    }

    private static async Task<string> RoundTripAsync(ElevatedRequest request, CancellationToken ct, int connectTimeoutMs)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ElevatedBrokerUnavailableException("Elevated broker is Windows-only.");
        }

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(connectTimeoutMs, ct).ConfigureAwait(false);
            await WriteFrameAsync(client, JsonSerializer.Serialize(request, Json), ct).ConfigureAwait(false);
            return await ReadFrameAsync(client, ct).ConfigureAwait(false);
        }
        catch (ElevatedBrokerUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ElevatedBrokerUnavailableException(
                $"Could not reach the elevated test broker on \\\\.\\pipe\\{PipeName()} "
                + "(start it via scripts/run-forge-ui.ps1, which launches it elevated). "
                + $"Underlying error: {ex.GetType().Name}: {ex.Message}",
                ex);
        }
    }

    // ── Length-prefixed framing (4-byte big-endian length + UTF-8 JSON) ─────

    private static async Task WriteFrameAsync(Stream s, string payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var len = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, bytes.Length);
        await s.WriteAsync(len, ct).ConfigureAwait(false);
        await s.WriteAsync(bytes, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadFrameAsync(Stream s, CancellationToken ct)
    {
        var len = await ReadExactAsync(s, 4, ct).ConfigureAwait(false);
        var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(len);
        if (count < 0 || count > 64 * 1024 * 1024)
        {
            throw new InvalidOperationException($"broker frame length out of range: {count}");
        }
        var body = await ReadExactAsync(s, count, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(body);
    }

    private static async Task<byte[]> ReadExactAsync(Stream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("broker pipe closed mid-frame");
            read += n;
        }
        return buf;
    }
}

/// <summary>Tagged request envelope: exactly one of Build/Script is set per Op.</summary>
public sealed record ElevatedRequest(string Op, ElevatedBuildRequest? Build, ElevatedScriptRequest? Script);

/// <summary>
/// Marshalled subset of <see cref="StoryBuildRunner.RunBuildAsync"/> parameters
/// (story test gate). The client builds the FINAL command (impacted-test
/// narrowing, exclude filter, log paths) before marshalling; the broker only
/// executes it elevated and returns the parsed result.
/// </summary>
public sealed record ElevatedBuildRequest(
    string Workspace,
    long? TimeoutMs,
    string? BuildCommand,
    string? LogFilePath,
    string? LogRelPath,
    IReadOnlyList<string>? SessionSetupCommands,
    bool CollectTestFailures,
    string? TrxScopeTag);

/// <summary>Build-op reply: the parsed result, or an error string on failure.</summary>
public sealed record ElevatedBuildResponse(StoryBuildResult? Result, string? Error);

/// <summary>Raw cmd-script run (repo validation): script text, cwd, timeout, optional log file.</summary>
public sealed record ElevatedScriptRequest(string Script, string Cwd, int TimeoutSeconds, string? LogPath);

/// <summary>Script-op reply: exit code + captured output, or an error string.</summary>
public sealed record ElevatedScriptResponse(int Code, string? Stdout, string? Stderr, bool TimedOut, string? Error);

/// <summary>
/// Thrown when the elevated broker is unreachable or errored. The test gate /
/// validation converts this into a stall — a
/// <see cref="Persistence.RepoRow.RunTestsElevated"/> repo must never silently
/// run its tests without elevation.
/// </summary>
public sealed class ElevatedBrokerUnavailableException : Exception
{
    public ElevatedBrokerUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
