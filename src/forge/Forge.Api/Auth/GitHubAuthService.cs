// -----------------------------------------------------------------------
// <copyright file="GitHubAuthService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 9.3 — GitHub OAuth Authorization Code + PKCE.
///
/// Flow:
///   1) Generate code_verifier (random 64 bytes, base64url) + code_challenge (S256).
///   2) Spin up a one-shot HttpListener on http://127.0.0.1:53682/forge/oauth/github/callback.
///   3) Launch the browser to GitHub /authorize with code_challenge + state.
///   4) Wait for the callback (3 min cap). Validate state. Reply with a "you can close this" page.
///   5) Exchange code at /login/oauth/access_token with code_verifier.
///   6) Persist the access token in DPAPI-protected blob at %LOCALAPPDATA%\Forge\github-token.bin.
///
/// Device-code flow is explicitly NOT used (Q7). The OAuth App must have PKCE
/// enabled and a client_id configured via Forge:Auth:GitHubClientId. Until the
/// operator registers an app, sign-in returns a helpful error and the existing
/// PAT path keeps working.
/// </summary>
public class GitHubAuthService : IAuthService
{
    public const string AuthHost = "https://github.com";

    public const string ApiHost = "https://api.github.com";

    public const int CallbackPort = 53682;

    public const string CallbackPath = "/forge/oauth/github/callback";

    private static readonly string TokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Forge",
        "github-token.bin");

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("forge-github-token-v1");

    private readonly ForgeAuthOptions options;
    private readonly IBrowserLauncher browser;
    private readonly IHttpClientFactory httpFactory;
    private readonly ILogger<GitHubAuthService> logger;

    private readonly object cacheGate = new();
    private TokenRecord? cached;

    public GitHubAuthService(
        IOptions<ForgeAuthOptions> options,
        IBrowserLauncher browser,
        IHttpClientFactory httpFactory,
        ILogger<GitHubAuthService> logger)
    {
        this.options = options.Value;
        this.browser = browser;
        this.httpFactory = httpFactory;
        this.logger = logger;
        this.cached = LoadFromDisk();
    }

    public string Kind => "github";

    public Task<AuthStatus> StatusAsync(CancellationToken ct = default)
    {
        var rec = this.GetCached();
        if (rec is null)
        {
            return Task.FromResult(new AuthStatus(this.Kind, false, null, null, null));
        }
        return Task.FromResult(new AuthStatus(this.Kind, true, rec.Login, rec.Login, rec.ExpiresAt));
    }

    public async Task<AuthStatus> SignInAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(this.options.GitHubClientId))
        {
            return new AuthStatus(
                this.Kind, false, null, null, null,
                "GitHub OAuth client id missing — register an OAuth App with PKCE enabled, then set Forge:Auth:GitHubClientId.");
        }

        var verifier = GenerateCodeVerifier();
        var challenge = ComputeCodeChallenge(verifier);
        var state = GenerateRandomString(24);
        var redirectUri = $"http://127.0.0.1:{CallbackPort}{CallbackPath}";

        // The HttpListener runs ONCE for this sign-in. We don't keep it bound
        // long-term so port 53682 is only held while a sign-in is in progress.
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{CallbackPort}{CallbackPath}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            return new AuthStatus(this.Kind, false, null, null, null,
                $"Could not bind localhost callback ({ex.Message}). Make sure no other Forge sign-in is in progress.");
        }

        var scopes = string.IsNullOrWhiteSpace(this.options.GitHubScopes) ? "repo read:user read:org" : this.options.GitHubScopes;
        var authorizeUrl =
            $"{AuthHost}/login/oauth/authorize" +
            $"?client_id={Uri.EscapeDataString(this.options.GitHubClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scopes)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            "&code_challenge_method=S256";

        if (!this.browser.Launch(authorizeUrl))
        {
            listener.Stop();
            return new AuthStatus(this.Kind, false, null, null, null, "Could not launch a browser for GitHub sign-in.");
        }

        // 3 minutes is plenty for a normal consent flow; cap so we don't orphan the listener.
        using var ctsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ctsTimeout.CancelAfter(TimeSpan.FromMinutes(3));

        HttpListenerContext context;
        try
        {
            var getCtx = listener.GetContextAsync();
            var done = await Task.WhenAny(getCtx, Task.Delay(Timeout.Infinite, ctsTimeout.Token));
            if (done != getCtx)
            {
                listener.Stop();
                return new AuthStatus(this.Kind, false, null, null, null, "GitHub sign-in timed out.");
            }
            context = await getCtx;
        }
        catch (Exception ex)
        {
            return new AuthStatus(this.Kind, false, null, null, null, $"Callback listener failed: {ex.Message}");
        }

        var code = context.Request.QueryString["code"];
        var returnedState = context.Request.QueryString["state"];
        var oauthError = context.Request.QueryString["error"];

        await WriteCloseTabPageAsync(context.Response, oauthError, code is not null);

        listener.Stop();

        if (!string.IsNullOrEmpty(oauthError))
        {
            return new AuthStatus(this.Kind, false, null, null, null, $"GitHub returned error: {oauthError}");
        }
        if (string.IsNullOrEmpty(code))
        {
            return new AuthStatus(this.Kind, false, null, null, null, "GitHub callback missing code parameter.");
        }
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(returnedState ?? string.Empty),
            Encoding.UTF8.GetBytes(state)))
        {
            return new AuthStatus(this.Kind, false, null, null, null, "OAuth state mismatch — request rejected.");
        }

        // Exchange code for token.
        var http = this.httpFactory.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["client_id"] = this.options.GitHubClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        };
        var req = new HttpRequestMessage(HttpMethod.Post, $"{AuthHost}/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return new AuthStatus(this.Kind, false, null, null, null, $"Token exchange failed: HTTP {(int)resp.StatusCode}");
        }
        var body = await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
        if (body?.access_token is null)
        {
            return new AuthStatus(this.Kind, false, null, null, null, $"Token exchange returned no access_token{(body?.error is null ? "" : $" ({body.error})")}.");
        }

        // Look up the login name for nicer UX.
        var login = await FetchLoginAsync(http, body.access_token, ct);
        var record = new TokenRecord(
            AccessToken: body.access_token,
            Login: login,
            ExpiresAt: body.expires_in is { } seconds ? DateTimeOffset.UtcNow.AddSeconds(seconds) : (DateTimeOffset?)null);

        this.SaveCache(record);
        return new AuthStatus(this.Kind, true, record.Login, record.Login, record.ExpiresAt);
    }

    public Task SignOutAsync(CancellationToken ct = default)
    {
        this.SaveCache(null);
        return Task.CompletedTask;
    }

    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var rec = this.GetCached();
        return Task.FromResult<string?>(rec?.AccessToken);
    }

    // -- helpers --------------------------------------------------------------

    private TokenRecord? GetCached()
    {
        lock (this.cacheGate)
        {
            return this.cached;
        }
    }

    private void SaveCache(TokenRecord? rec)
    {
        lock (this.cacheGate)
        {
            this.cached = rec;
            try { PersistToDisk(rec); }
            catch (Exception ex) { this.logger.LogWarning(ex, "GitHub token persistence failed"); }
        }
    }

    private static TokenRecord? LoadFromDisk()
    {
        if (!File.Exists(TokenPath)) return null;
        try
        {
            var blob = File.ReadAllBytes(TokenPath);
            var json = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ProtectedData.Unprotect(blob, DpapiEntropy, DataProtectionScope.CurrentUser)
                : blob;
            return JsonSerializer.Deserialize<TokenRecord>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void PersistToDisk(TokenRecord? rec)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        if (rec is null)
        {
            if (File.Exists(TokenPath)) File.Delete(TokenPath);
            return;
        }
        var json = JsonSerializer.SerializeToUtf8Bytes(rec);
        var blob = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ProtectedData.Protect(json, DpapiEntropy, DataProtectionScope.CurrentUser)
            : json;
        File.WriteAllBytes(TokenPath, blob);
    }

    private static string GenerateCodeVerifier()
    {
        // RFC 7636 §4.1 — code_verifier is 43..128 chars, unreserved.
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Base64UrlEncode(bytes);
    }

    private static string ComputeCodeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(bytes);
    }

    private static string GenerateRandomString(int byteCount)
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string?> FetchLoginAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiHost}/user");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.UserAgent.ParseAdd("forge-api/0.1");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("login", out var l) ? l.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteCloseTabPageAsync(HttpListenerResponse response, string? error, bool ok)
    {
        var body = error is null && ok
            ? "<html><body style=\"font-family:system-ui;text-align:center;padding-top:80px\"><h2>Forge — signed in</h2><p>You can close this tab.</p></body></html>"
            : $"<html><body style=\"font-family:system-ui;text-align:center;padding-top:80px\"><h2>Forge — sign-in failed</h2><p>{System.Net.WebUtility.HtmlEncode(error ?? "missing code")}</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.OutputStream.Close();
    }

    private sealed record TokenRecord(string AccessToken, string? Login, DateTimeOffset? ExpiresAt);

#pragma warning disable IDE1006 // GitHub uses snake_case in the JSON body.
    private sealed class TokenResponse
    {
        public string? access_token { get; set; }
        public string? token_type { get; set; }
        public int? expires_in { get; set; }
        public string? scope { get; set; }
        public string? error { get; set; }
    }
#pragma warning restore IDE1006
}
