// -----------------------------------------------------------------------
// <copyright file="ChatActions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat;

using System.Text.Json;

/// <summary>
/// Serialize/parse an inline-action button set to/from a compact JSON array —
/// the shape apps persist alongside a queued notification so the buttons
/// survive until the outbound send.
/// </summary>
public static class ChatActions
{
    /// <summary>Serialize buttons to the compact JSON array (null when empty).</summary>
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
