// -----------------------------------------------------------------------
// <copyright file="ChatAuthorizationProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>Never expose provider response bodies or token-bearing Telegram URLs in diagnostics.</summary>
public sealed class ChatAuthorizationProvider(IHttpClientFactory clients)
{
    public const string ClientName = "chat-authorization";
    internal const string SlackCliVersion = "v4.7.0";
    internal static readonly string[] SlackScopes = ["chat:write", "channels:read", "channels:history"];

    internal async Task<JsonElement> SlackAsync(SlackAuthorizationOptions options, string secret, string code, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/oauth.v2.access");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(options.ClientId + ":" + secret)));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["redirect_uri"] = options.RedirectUri });
        return await this.SendAsync(request, ct);
    }

    internal async Task<JsonElement> TelegramAsync(string token, string method, object body, CancellationToken ct)
    {
        if (token.Length > 256 || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '-') || !token.Contains(':'))
            throw new ChatConfigurationException("authorization-registration-required", 409);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.telegram.org/bot" + token + "/" + method);
        request.Content = JsonContent.Create(body);
        return await this.SendAsync(request, ct);
    }

    // Slack CLI v4.7's provisioning protocol. Endpoints and credentials never come from browser input.
    internal async Task<JsonElement> SlackSetupAsync(string method, string? token, object body, CancellationToken ct)
    {
        if (method is not ("apps.hosted.generateAuthTicket" or "apps.hosted.exchangeAuthTicket" or
            "apps.manifest.create" or "apps.developerInstall" or "auth.test" or "apps.connections.open"))
            throw new ArgumentOutOfRangeException(nameof(method));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/" + method);
        // Slack's ticket endpoint returns internal_error without CLI protocol identification.
        // Identify Forge as well as the compatible CLI protocol; never imitate a browser session.
        request.Headers.UserAgent.ParseAdd("slack-cli/" + SlackCliVersion + " (os: "
            + (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux") + ") Forge/1.0");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = method.StartsWith("apps.hosted.", StringComparison.Ordinal)
            ? new FormUrlEncodedContent(JsonSerializer.SerializeToElement(body).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!))
            : JsonContent.Create(body);
        return await this.SendAsync(request, ct);
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        ct = timeout.Token;
        try
        {
            using var client = clients.CreateClient(ClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (request.RequestUri?.Host == "api.telegram.org" && response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.NotFound)
                throw new ChatConfigurationException("authorization-telegram-token-rejected", 400);
            if (!response.IsSuccessStatusCode) throw new ChatConfigurationException("authorization-provider-unavailable", 502);
            // A malicious/error response must not allocate unbounded memory or reach exception logs.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > 256 * 1024) throw new ChatConfigurationException("authorization-provider-rejected");
                buffer.Write(chunk, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var result = document.RootElement;
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                if (request.RequestUri?.Host == "slack.com" && String(result, "error") == "internal_error")
                    throw new ChatConfigurationException("authorization-provider-unavailable", 502);
                if (request.RequestUri?.Host == "slack.com" && request.RequestUri.AbsolutePath == "/api/apps.developerInstall"
                    && String(result, "error") is "app_approval_request_eligible" or "app_approval_request_pending" or "app_approval_request_denied")
                    throw new ChatConfigurationException("authorization-slack-admin-approval-required", 409);
                throw new ChatConfigurationException("authorization-provider-rejected", 400);
            }
            return result.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            // Cancellation also discards an OAuth code: its exchange may have reached the provider.
            throw new ChatConfigurationException("authorization-provider-unavailable", 502);
        }
    }

    internal static string? String(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    internal static string? Number(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number
            && field.TryGetInt64(out var id) && id > 0 ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    internal static bool True(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.True;
}