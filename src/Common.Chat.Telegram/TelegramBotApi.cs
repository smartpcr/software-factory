// -----------------------------------------------------------------------
// <copyright file="TelegramBotApi.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat.Telegram;

using System.Net.Http.Json;
using System.Text.Json;

/// <summary>Fixed-origin Bot API calls. Exceptions, response descriptions and token-bearing URLs never escape.</summary>
internal sealed class TelegramBotApi(IHttpClientFactory clients, string token)
{
    public async Task<ChatProviderResult<JsonElement>> CallAsync(string method, object body, CancellationToken ct, bool mayCreate = false)
    {
        try
        {
            using var client = clients.CreateClient(TelegramProvider.ClientName);
            using var response = await client.PostAsJsonAsync($"https://api.telegram.org/bot{token}/{method}", body, ct);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            if (response.IsSuccessStatusCode && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("result", out var result)) return ChatProviderResult<JsonElement>.Success(result.Clone());
            var status = root.TryGetProperty("error_code", out var code) && code.TryGetInt32(out var number) ? number : (int)response.StatusCode;
            var description = root.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String ? desc.GetString() : "";
            if (method.StartsWith("edit", StringComparison.Ordinal) && description?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true
                || method == "closeForumTopic" && description?.Contains("TOPIC_NOT_MODIFIED", StringComparison.OrdinalIgnoreCase) == true)
                return ChatProviderResult<JsonElement>.Success(JsonSerializer.SerializeToElement(true));
            if (status == 429)
            {
                var seconds = root.TryGetProperty("parameters", out var p) && p.TryGetProperty("retry_after", out var retry) && retry.TryGetInt32(out var delay) ? Math.Clamp(delay, 1, 86400) : 30;
                return ChatProviderResult<JsonElement>.Fail(ChatFailureKind.RateLimited, "telegram-rate-limited", TimeSpan.FromSeconds(seconds));
            }
            return ChatProviderResult<JsonElement>.Fail(status switch
            {
                401 or 403 => ChatFailureKind.Unauthorized, 409 => ChatFailureKind.InvalidConfiguration,
                >= 500 => mayCreate ? ChatFailureKind.UnknownOutcome : ChatFailureKind.Unavailable, _ => ChatFailureKind.InvalidConfiguration,
            }, status switch { 401 => "telegram-token-revoked", 403 => "telegram-membership-denied", 409 => "telegram-receiver-conflict", _ => "telegram-request-failed" });
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException or IOException)
        { return ChatProviderResult<JsonElement>.Fail(mayCreate ? ChatFailureKind.UnknownOutcome : ChatFailureKind.Unavailable, "telegram-request-unconfirmed"); }
    }

    internal static string? String(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.Number or JsonValueKind.String ? value.ToString() : null;
    internal static bool True(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}