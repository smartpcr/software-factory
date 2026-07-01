// -----------------------------------------------------------------------
// <copyright file="ChatMessageRenderer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Forge.Api.Persistence;

/// <summary>
/// Phase 95 (chat bridge) — turns a <see cref="ChatNotificationRow"/> into the
/// text + inline buttons the transport sends, and the "resolved" text the
/// outbound pump edits in once the item is answered/acted elsewhere.
/// </summary>
public static class ChatMessageRenderer
{
    /// <summary>Body text + inline buttons for the initial send.</summary>
    public static (string Text, IReadOnlyList<ChatButton>? Buttons) Render(ChatNotificationRow row)
    {
        return (BaseText(row), ChatActions.Parse(row.ActionsJson));
    }

    /// <summary>Text edited into a sent message once the item is resolved (buttons dropped).</summary>
    public static string RenderResolved(ChatNotificationRow row) => $"{BaseText(row)}\n\n✅ Resolved";

    private static string BaseText(ChatNotificationRow row) =>
        string.IsNullOrWhiteSpace(row.Body) ? row.Title : $"{row.Title}\n\n{row.Body}";
}

/// <summary>
/// Serialize/parse the inline-action set stored in
/// <see cref="ChatNotificationRow.ActionsJson"/>.
/// </summary>
public static class ChatActions
{
    /// <summary>Serialize buttons to the compact JSON array stored on the row (null when empty).</summary>
    public static string? Serialize(IReadOnlyList<ChatButton>? buttons)
    {
        if (buttons is not { Count: > 0 })
        {
            return null;
        }

        return JsonSerializer.Serialize(buttons.Select(b => new ButtonDto(b.Label, b.CallbackData)).ToList());
    }

    /// <summary>Parse the stored JSON back into buttons (null on empty/malformed).</summary>
    public static IReadOnlyList<ChatButton>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var dtos = JsonSerializer.Deserialize<List<ButtonDto>>(json);
            if (dtos is null || dtos.Count == 0)
            {
                return null;
            }

            return dtos
                .Where(d => !string.IsNullOrEmpty(d.Label) && !string.IsNullOrEmpty(d.CallbackData))
                .Select(d => new ChatButton(d.Label, d.CallbackData))
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ButtonDto(string Label, string CallbackData);
}
