// -----------------------------------------------------------------------
// <copyright file="OpenQuestionsExtractor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Phase 39 (Layer C) — parse the structured open-questions block out of a
/// generator turn's raw text. The architect's prompt template instructs the
/// generator to emit (when it has questions) a fenced JSON block of the
/// form:
/// <code>
/// ```json open-questions
/// { "openQuestions": [
///     { "id": "auth-method", "text": "Which auth flow?", "type": "choice", "choices": ["OAuth", "API key"] },
///     { "id": "retry-policy", "text": "Cap retries?", "type": "open" }
/// ] }
/// ```
/// </code>
/// Operator rule: a question is never shown without options. Agents are told to emit
/// only <c>choice</c> questions; anything parsed here as <c>open</c> (or a choice with
/// fewer than two options) gets options from <see cref="QuestionOptions.EnsureAsync"/>
/// before it is stored or shown.
/// We accept either <c>```json open-questions</c> or just <c>```json</c> so a
/// model that drops the language tag still works. Returns an empty list when
/// no block is found, the JSON is malformed, or the array is empty.
/// </summary>
public static class OpenQuestionsExtractor
{
    /// <summary>One parsed question entry. <paramref name="ExplanationJson"/>
    /// is the serialized operator-facing rationale object (terms, per-choice
    /// logic, recommendation) when the agent supplied any of those fields;
    /// null otherwise. It maps straight onto
    /// <see cref="Forge.Api.Persistence.DeliverableQuestionRow.ExplanationJson"/>.</summary>
    public sealed record OpenQuestion(
        string ExternalId, string Text, string Kind, IReadOnlyList<string>? Choices, string? ExplanationJson = null);

    /// <summary>Match a fenced JSON block whose body contains an "openQuestions" key.</summary>
    private static readonly Regex JsonFence = new Regex(
        @"```(?:json[^\n]*)?\s*\r?\n(?<body>\{[\s\S]*?""openQuestions""[\s\S]*?\})\s*\r?\n```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Try the structured JSON-block path first (preferred — produces typed
    /// choice/open questions with explicit option lists). Fall back to the
    /// plain markdown <c>## Open Questions</c> section format the architect
    /// emitted historically (numbered or bulleted list under the heading).
    /// Markdown-extracted questions are always type=open with synthetic ids
    /// derived from the first words of the question text so re-emissions
    /// dedupe stably across iterations.
    /// </summary>
    public static IReadOnlyList<OpenQuestion> Extract(string? generatorText)
    {
        if (string.IsNullOrWhiteSpace(generatorText)) return Array.Empty<OpenQuestion>();

        // 1) Preferred — structured JSON block.
        var fromJson = ExtractFromJsonBlock(generatorText);
        if (fromJson.Count > 0) return fromJson;

        // 2) Fallback — `## Open Questions` markdown section (or any heading
        //    containing "Open Questions" / "Open questions" — case-insensitive).
        return ExtractFromMarkdown(generatorText);
    }

    private static IReadOnlyList<OpenQuestion> ExtractFromJsonBlock(string generatorText)
    {
        var match = JsonFence.Match(generatorText);
        if (!match.Success) return Array.Empty<OpenQuestion>();

        var body = match.Groups["body"].Value;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("openQuestions", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<OpenQuestion>();

            var result = new List<OpenQuestion>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ix = 0;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { ix++; continue; }
                var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString() ?? $"q{ix + 1}"
                    : $"q{ix + 1}";
                // De-dupe within a single emission (the model occasionally
                // repeats an entry); the per-iter index disambiguates across
                // iterations downstream.
                if (!seenIds.Add(id)) { ix++; continue; }

                var text = item.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String
                    ? txt.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(text)) { ix++; continue; }

                var kindRaw = item.TryGetProperty("type", out var k) && k.ValueKind == JsonValueKind.String
                    ? k.GetString()
                    : null;
                var kind = string.Equals(kindRaw, "choice", StringComparison.OrdinalIgnoreCase)
                    ? "choice"
                    : "open";

                List<string>? choices = null;
                if (kind == "choice"
                    && item.TryGetProperty("choices", out var choicesEl)
                    && choicesEl.ValueKind == JsonValueKind.Array)
                {
                    choices = new List<string>();
                    foreach (var c in choicesEl.EnumerateArray())
                    {
                        if (c.ValueKind == JsonValueKind.String)
                        {
                            var s = c.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) choices.Add(s!);
                        }
                    }
                    if (choices.Count == 0)
                    {
                        // Choice with no options → demote to open so the UI
                        // doesn't render an empty radio group.
                        kind = "open";
                        choices = null;
                    }
                }

                var explanationJson = BuildExplanationJson(item);
                result.Add(new OpenQuestion(id, text.Trim(), kind, choices, explanationJson));
                ix++;
            }
            return result;
        }
        catch (JsonException)
        {
            // Malformed JSON in the fence — treat as no questions rather
            // than failing the iteration. The architect can re-emit
            // correctly on the next round.
            return Array.Empty<OpenQuestion>();
        }
    }

    /// <summary>
    /// Build the serialized operator-facing rationale object from an agent's
    /// open-question JSON entry. Reads <c>explanation</c> (string),
    /// <c>choiceExplanations</c> (object: choice → rationale),
    /// <c>recommendation</c> (string), and <c>recommendationRationale</c>
    /// (string). Returns null when the agent supplied none of them (the
    /// Explain button then offers on-demand generation). The shape mirrors
    /// what <c>OpenQuestionsWizard</c> renders and what the
    /// <c>/questions/{externalId}/explain</c> endpoint writes.
    /// </summary>
    private static string? BuildExplanationJson(JsonElement item)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (item.TryGetProperty("explanation", out var expl) && expl.ValueKind == JsonValueKind.String)
        {
            var s = expl.GetString();
            if (!string.IsNullOrWhiteSpace(s)) payload["explanation"] = s.Trim();
        }

        // Preferred per-choice structure: choiceAnalysis [{ choice, pros[], cons[] }].
        if (item.TryGetProperty("choiceAnalysis", out var ca) && ca.ValueKind == JsonValueKind.Array)
        {
            var list = new List<object>();
            foreach (var entry in ca.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var choice = entry.TryGetProperty("choice", out var ch) && ch.ValueKind == JsonValueKind.String
                    ? ch.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(choice)) continue;
                list.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["choice"] = choice,
                    ["pros"] = ReadStringArray(entry, "pros"),
                    ["cons"] = ReadStringArray(entry, "cons"),
                });
            }
            if (list.Count > 0) payload["choiceAnalysis"] = list;
        }

        // Back-compat: accept a flat choiceExplanations map too.
        if (item.TryGetProperty("choiceExplanations", out var ce) && ce.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in ce.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var v = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) map[prop.Name] = v.Trim();
                }
            }
            if (map.Count > 0) payload["choiceExplanations"] = map;
        }

        if (item.TryGetProperty("recommendation", out var rec) && rec.ValueKind == JsonValueKind.String)
        {
            var s = rec.GetString();
            if (!string.IsNullOrWhiteSpace(s)) payload["recommendation"] = s.Trim();
        }

        if (item.TryGetProperty("recommendationRationale", out var rr) && rr.ValueKind == JsonValueKind.String)
        {
            var s = rr.GetString();
            if (!string.IsNullOrWhiteSpace(s)) payload["recommendationRationale"] = s.Trim();
        }

        // Choices that park the work ("stop and wait for something outside it"); only
        // ones that are among the question's own choices count (QuestionOptions.ParkInForce).
        var parks = ReadStringArray(item, "parks");
        var choices = ReadStringArray(item, "choices");
        parks.RemoveAll(p => !choices.Contains(p, StringComparer.OrdinalIgnoreCase));
        if (parks.Count > 0) payload["parks"] = parks;

        if (payload.Count == 0) return null;
        payload["generated"] = false; // agent-authored
        return JsonSerializer.Serialize(payload);
    }

    private static List<string> ReadStringArray(JsonElement obj, string name)
    {
        var result = new List<string>();
        if (obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in arr.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    result.Add(v.GetString()!.Trim());
        }
        return result;
    }

    /// <summary>
    /// Match a markdown heading containing "Open Questions" (case-insensitive).
    /// Captures the body up to the next ## heading, end of doc, or fenced
    /// code block. Used as the markdown-section fallback.
    /// </summary>
    private static readonly Regex OpenQuestionsHeading = new Regex(
        @"^#{1,6}\s*(?:[\d§\.]+\s*)?Open\s+Questions\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex ListItem = new Regex(
        // Numbered (1. / 1)) or bulleted (- * +) list item, possibly indented.
        @"^\s*(?:\d{1,3}[\.\)]|[-*+])\s+(?<body>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static IReadOnlyList<OpenQuestion> ExtractFromMarkdown(string text)
    {
        var hMatch = OpenQuestionsHeading.Match(text);
        if (!hMatch.Success) return Array.Empty<OpenQuestion>();

        // Body = from end of heading line to next ## heading or end of doc.
        var afterHeading = hMatch.Index + hMatch.Length;
        var nextHeading = NextSiblingHeading.Match(text, afterHeading);
        var bodyEnd = nextHeading.Success ? nextHeading.Index : text.Length;
        var body = text[afterHeading..bodyEnd];

        // Walk list items. Each item may span multiple lines until the next
        // list marker or a blank line followed by non-list content.
        var items = new List<string>();
        var lastEnd = 0;
        var matches = ListItem.Matches(body);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            // Capture from the start of this match to the start of the next
            // (or end of body) so multi-line items keep their continuation.
            var start = m.Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;
            var raw = body[start..end].Trim();
            // Strip the leading marker so the question text starts clean.
            var stripped = ListItem.Replace(raw, "${body}", count: 1);
            // Collapse whitespace runs but preserve sentence boundaries.
            stripped = Regex.Replace(stripped, @"\s+", " ").Trim();
            if (!string.IsNullOrWhiteSpace(stripped)) items.Add(stripped);
            lastEnd = end;
        }

        if (items.Count == 0) return Array.Empty<OpenQuestion>();

        var result = new List<OpenQuestion>(items.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ix = 1;
        foreach (var rawBullet in items)
        {
            // Tighten the question text: a single bullet often mixes an
            // intro phrase, the actual question, and surrounding analysis.
            // Keep only the sentence that ends in `?` (the architect's
            // legacy free-form prompt produced bullets like
            // `**Topic.** Should X or Y? Some context. Initial stance: …`).
            // If no `?` is present, skip the bullet — it's an observation,
            // not an answerable question, and surfacing it would just
            // confuse the operator (the new structured-JSON prompt
            // template prevents this for future iterations).
            var qText = ExtractQuestionSentence(rawBullet);
            if (string.IsNullOrWhiteSpace(qText)) { ix++; continue; }

            var id = SlugifyForId(qText, fallbackIndex: ix);
            // Dedupe within a single emission so a model that repeated a
            // bullet doesn't produce two rows.
            if (!seen.Add(id))
            {
                ix++;
                continue;
            }
            result.Add(new OpenQuestion(id, qText, "open", null));
            ix++;
        }
        return result;
    }

    /// <summary>
    /// Pull the actual question out of a bullet. Strips the leading
    /// `**Topic.**` label; takes the first sentence that ends in `?`. Caps
    /// at 240 chars so a runaway sentence doesn't blow the wizard layout.
    /// Returns empty string when the bullet has no `?` at all (caller
    /// drops it).
    /// </summary>
    private static string ExtractQuestionSentence(string bullet)
    {
        if (string.IsNullOrWhiteSpace(bullet)) return string.Empty;
        // Strip the `**Topic.**` lead-in if present, plus a trailing space.
        var labelStripped = Regex.Replace(bullet, @"^\*\*[^*]+\*\*\s*", string.Empty);

        // Find the first sentence ending in `?`. Sentence boundaries here
        // are simplistic — split on `?` followed by a space or end-of-text.
        var qIdx = labelStripped.IndexOf('?');
        if (qIdx < 0) return string.Empty;

        // Walk backwards from `?` to the previous sentence boundary
        // (`.`, `!`, `?`) so we don't include preamble like
        // "X is fragile. Should we Y?" — we want only "Should we Y?".
        var start = 0;
        for (var i = qIdx - 1; i >= 0; i--)
        {
            var ch = labelStripped[i];
            if (ch == '.' || ch == '!' || ch == '?')
            {
                start = i + 1;
                break;
            }
        }
        var sentence = labelStripped[start..(qIdx + 1)].Trim();
        // Final tidy — strip leading bullet leftovers, collapse whitespace.
        sentence = Regex.Replace(sentence, @"\s+", " ");
        if (sentence.Length > 240) sentence = sentence[..240].TrimEnd() + "…";
        return sentence;
    }

    private static readonly Regex NextSiblingHeading = new Regex(
        @"^#{1,6}\s+\S",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Convert "Embedding model selection. Should we use..." → "embedding-model-selection".
    /// First-words slug keeps ids stable across re-emissions of the same
    /// question text (the model rarely re-words intros).
    /// </summary>
    private static string SlugifyForId(string text, int fallbackIndex)
    {
        // Take up to the first sentence-ending punctuation, capped at 60 chars.
        var stop = text.IndexOfAny(new[] { '.', '?', '!', '\n' });
        var head = (stop > 0 ? text[..stop] : text).Trim();
        if (head.Length > 60) head = head[..60];
        var slug = Regex.Replace(head.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length < 3) return $"q{fallbackIndex}";
        // Cap the slug length so it stays a reasonable id.
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        return slug;
    }
}
