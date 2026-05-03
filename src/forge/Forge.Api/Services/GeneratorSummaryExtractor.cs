// -----------------------------------------------------------------------
// <copyright file="GeneratorSummaryExtractor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System;

/// <summary>
/// Pulls the iteration summary out of a generator turn's raw text. The
/// generator is instructed to end its response with one of three section
/// headings — <c>## Iteration Summary</c>, <c>## Change Summary</c>, or
/// <c>## Plan Summary</c> — and we extract whichever is present so the
/// per-iteration record stores the section the agent wrote rather than an
/// arbitrary tail of the response.
/// </summary>
/// <remarks>
/// Until 2026-05-03 the loop stored <c>gen.OutputText[^800..]</c> directly,
/// which only happened to capture the section when it was short enough to
/// fit in the tail. WI-1035's iter-3 summary fell outside that window and
/// the Summary tab would have shown junk.
/// </remarks>
public static class GeneratorSummaryExtractor
{
    private static readonly string[] Headings = new[]
    {
        "## Iteration Summary",
        "## Change Summary",
        "## Plan Summary",
    };

    /// <summary>
    /// Extract the summary section. Returns the body of the first matching
    /// heading; if no heading is present, falls back to the last
    /// <paramref name="fallbackTailChars"/> of the input so something useful
    /// is recorded even when the agent forgets to follow the format.
    /// </summary>
    public static string Extract(string? generatorText, int maxChars = 4000, int fallbackTailChars = 4000)
    {
        if (string.IsNullOrWhiteSpace(generatorText)) return string.Empty;

        var headingIndex = -1;
        var headingLength = 0;
        foreach (var h in Headings)
        {
            var idx = generatorText.IndexOf(h, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (headingIndex < 0 || idx < headingIndex))
            {
                headingIndex = idx;
                headingLength = h.Length;
            }
        }

        string body;
        if (headingIndex >= 0)
        {
            body = generatorText[(headingIndex + headingLength)..].TrimStart('\r', '\n', ' ', '\t');
            // Truncate at the next ## heading so we don't bleed into a
            // sibling section the agent appended after the summary.
            var nextHeading = body.IndexOf("\n## ", StringComparison.Ordinal);
            if (nextHeading > 0) body = body[..nextHeading];
            body = body.TrimEnd();
        }
        else
        {
            // No heading — keep the tail as a best-effort fallback.
            body = generatorText.Length > fallbackTailChars
                ? generatorText[^fallbackTailChars..]
                : generatorText;
        }

        if (body.Length > maxChars) body = body[..maxChars];
        return body;
    }
}
