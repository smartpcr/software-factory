// -----------------------------------------------------------------------
// <copyright file="ChatMessageRenderer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Common.Chat;
using Forge.Api.Persistence;

/// <summary>
/// Phase 95 (chat bridge) — turns a <see cref="ChatNotificationRow"/> into the
/// text + inline buttons the transport sends, and the "resolved" text the
/// outbound pump edits in once the item is answered/acted elsewhere.
/// Button JSON (de)serialization lives in <see cref="ChatActions"/> (Common.Chat).
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
