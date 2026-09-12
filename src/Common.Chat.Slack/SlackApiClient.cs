// -----------------------------------------------------------------------
// <copyright file="SlackApiClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat.Slack;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>Fixed origin, header credentials, safe failures, and no automatic replay of message creation.</summary>
internal sealed class SlackApiClient(IHttpClientFactory clients, string token, SlackRequestBudget budget, bool appToken = false)
{
    public async Task<ChatProviderResult<JsonElement>> CallAsync(string method, object body, CancellationToken ct, string? channel = null)
    {
        ct.ThrowIfCancellationRequested();
        if (await budget.EnterAsync(method, channel, ct) is { } delay)
            return ChatProviderResult<JsonElement>.Fail(ChatFailureKind.RateLimited, "slack-rate-limited", delay);
        var creating = method is "chat.postMessage" or "chat.postEphemeral";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/" + method);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // Slack's conversation reads must receive encoded parameters: the live
            // endpoint can ignore JSON bodies even while returning HTTP 200.
            request.Content = method switch
            {
                "apps.connections.open" => new FormUrlEncodedContent([]),
                "conversations.info" or "conversations.list" => new FormUrlEncodedContent(JsonSerializer.SerializeToElement(body)
                    .EnumerateObject().Select(field => new KeyValuePair<string, string>(field.Name,
                        field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString()! : field.Value.GetRawText()))),
                _ => new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            using var client = clients.CreateClient(SlackProviderPlugin.ClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - budget.Now) ?? TimeSpan.FromSeconds(30);
                retry = TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 1, 86400));
                budget.Block(method, retry);
                return ChatProviderResult<JsonElement>.Fail(ChatFailureKind.RateLimited, "slack-rate-limited", retry);
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Failure("invalid_auth", appToken);
            if (!response.IsSuccessStatusCode)
                return ChatProviderResult<JsonElement>.Fail(creating ? ChatFailureKind.UnknownOutcome : ChatFailureKind.Unavailable, "slack-api-unavailable");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream(); var chunk = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > 1024 * 1024) throw new JsonException();
                buffer.Write(chunk, 0, read);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            if (!True(json.RootElement, "ok"))
            {
                var error = String(json.RootElement, "error");
                if (error is "ratelimited" or "rate_limited") budget.Block(method, TimeSpan.FromSeconds(30));
                if (creating && error is "internal_error" or "fatal_error" or "request_timeout" or "service_unavailable")
                    return ChatProviderResult<JsonElement>.Fail(ChatFailureKind.UnknownOutcome, "slack-send-outcome-unknown");
                return Failure(error, appToken);
            }
            return ChatProviderResult<JsonElement>.Success(json.RootElement.Clone());
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        { return ChatProviderResult<JsonElement>.Fail(creating ? ChatFailureKind.UnknownOutcome : ChatFailureKind.Unavailable, "slack-api-unavailable"); }
    }

    internal static ChatProviderResult<JsonElement> Failure(string? error, bool app) => error switch
    {
        "invalid_auth" or "not_authed" or "token_revoked" or "token_expired" or "account_inactive"
            => ChatProviderResult<JsonElement>.Fail(ChatFailureKind.Unauthorized, app ? "slack-app-token-revoked" : "slack-bot-token-revoked"),
        "missing_scope" or "not_allowed_token_type" => ChatProviderResult<JsonElement>.Fail(ChatFailureKind.InvalidConfiguration, app ? "slack-app-scope-required" : "slack-bot-scope-required"),
        "not_in_channel" or "no_permission" => ChatProviderResult<JsonElement>.Fail(ChatFailureKind.Unauthorized, "slack-membership-required"),
        "user_not_in_channel" => ChatProviderResult<JsonElement>.Fail(ChatFailureKind.InvalidConfiguration, "slack-link-user-not-in-channel"),
        "user_not_found" => ChatProviderResult<JsonElement>.Fail(ChatFailureKind.InvalidConfiguration, "invalid-link-target"),
        "is_archived" => ChatProviderResult<JsonElement>.Fail(ChatFailureKind.InvalidConfiguration, "slack-channel-archived"),
        "channel_not_found" => ChatProviderResult<JsonElement>.Fail(ChatFailureKind.InvalidConfiguration, "slack-channel-unavailable"),
        "team_access_not_granted" or "team_not_found" or "org_login_required" or "enterprise_is_restricted"
            => ChatProviderResult<JsonElement>.Fail(ChatFailureKind.InvalidConfiguration, "slack-workspace-unavailable"),
        "ratelimited" or "rate_limited" => ChatProviderResult<JsonElement>.Fail(ChatFailureKind.RateLimited, "slack-rate-limited", TimeSpan.FromSeconds(30)),
        "not_enabled" => ChatProviderResult<JsonElement>.Fail(ChatFailureKind.InvalidConfiguration, "slack-socket-disabled"),
        "invalid_blocks" or "msg_too_long" or "invalid_arguments" => ChatProviderResult<JsonElement>.Fail(ChatFailureKind.InvalidPayload, "slack-payload-invalid"),
        "cant_update_message" or "message_not_found" => ChatProviderResult<JsonElement>.Fail(ChatFailureKind.Unauthorized, "slack-message-unavailable"),
        _ => ChatProviderResult<JsonElement>.Fail(ChatFailureKind.Unavailable, "slack-api-unavailable"),
    };

    // External IDs must never be coerced from JSON numbers, including Slack message timestamps.
    internal static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    internal static JsonElement Object(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) ? field : default;
    internal static bool True(JsonElement value, string name) => Object(value, name).ValueKind == JsonValueKind.True;
    internal static bool Id(string? value) => value is { Length: > 0 and <= 128 } && value.All(char.IsAsciiLetterOrDigit);
    internal static bool Timestamp(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiDigit(c) || c == '.') && value.Count(c => c == '.') == 1 && value[0] != '.' && value[^1] != '.';
}

/// <summary>Shared across revisions for one installation. Workspace/method cooldowns never block a different workspace.</summary>
internal sealed class SlackRequestBudget(TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> next = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> blocked = new(StringComparer.Ordinal);
    public DateTimeOffset Now => clock.GetUtcNow();
    public void Block(string method, TimeSpan delay) => this.blocked.AddOrUpdate(method, this.Now + delay, (_, old) => old > this.Now + delay ? old : this.Now + delay);
    public async Task<TimeSpan?> EnterAsync(string method, string? channel, CancellationToken ct)
    {
        if (this.Cooldown(method) is { } cooldown) return cooldown;
        TimeSpan wait;
        lock (this.gate)
        {
            var now = this.Now;
            var at = this.next.GetValueOrDefault(method, now);
            if (channel is not null && this.next.GetValueOrDefault("channel:" + channel, now) is var channelAt && channelAt > at) at = channelAt;
            if (at < now) at = now;
            this.next[method] = at.AddSeconds(1);
            if (channel is not null) this.next["channel:" + channel] = at.AddSeconds(1);
            wait = at - now;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, ct);
        return this.Cooldown(method);
    }
    private TimeSpan? Cooldown(string method) => this.blocked.TryGetValue(method, out var until) && until > this.Now ? until - this.Now : null;
}