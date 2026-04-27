// -----------------------------------------------------------------------
// <copyright file="EvaluatorScoreParser.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Phase 14.3 — pure parser for an evaluator's last assistant message.
/// The locked JSON shape is documented in the spec at §2:
/// <code>
/// {
///   "score": 0..100,
///   "dims": { "correctness": 0..40, "quality": 0..25,
///             "tests": 0..15, "scope": 0..10, "risk": 0..10 },
///   "verdict": "pass" | "iterate" | "reject",
///   "notes": "&lt;= 800 chars"
/// }
/// </code>
/// <para>
/// The model returns this inside a fenced <c>```json</c> block at the end of
/// its response; we extract the block, parse it, and validate. If anything
/// fails we fall back to a deterministic <c>iterate</c>-with-score-0 result
/// and set <see cref="EvaluatorScore.ParseFailed"/> so the dispatcher can
/// emit a <c>forge.eval.parse_failed</c> telemetry event.
/// </para>
/// <para>
/// Verdict is **derived** from score (<see cref="ScoringConstants.PassThreshold"/>
/// / <see cref="ScoringConstants.IterateThreshold"/>) — the model's stated
/// verdict is recorded in <see cref="EvaluatorScore.ModelVerdict"/> for the
/// drift counter but never overrides the math.
/// </para>
/// </summary>
public static class EvaluatorScoreParser
{
    /// <summary>Locked rubric dimensions + their max weight.</summary>
    private static readonly (string Key, int Max)[] Dims =
    {
        ("correctness", ScoringConstants.CorrectnessWeight),
        ("quality",     ScoringConstants.QualityWeight),
        ("tests",       ScoringConstants.TestsWeight),
        ("scope",       ScoringConstants.ScopeWeight),
        ("risk",        ScoringConstants.RiskWeight),
    };

    private static readonly Regex JsonBlockRegex = new(
        @"```(?:json)?\s*\r?\n?(?<body>\{[\s\S]*?\})\s*\r?\n?```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareJsonObjectRegex = new(
        @"(?<body>\{[\s\S]*\})",
        RegexOptions.Compiled);

    private const int MaxNotesChars = 800;

    /// <summary>
    /// Parse an evaluator's assistant text into a structured score. Always
    /// returns a non-null result; <see cref="EvaluatorScore.ParseFailed"/>
    /// is true when the input was malformed.
    /// </summary>
    public static EvaluatorScore Parse(string? assistantText)
    {
        if (string.IsNullOrWhiteSpace(assistantText)) return Fallback("empty assistant text");

        var blockMatch = JsonBlockRegex.Match(assistantText);
        var jsonText = blockMatch.Success
            ? blockMatch.Groups["body"].Value
            : BareJsonObjectRegex.Match(assistantText) is { Success: true } bare ? bare.Groups["body"].Value
            : null;

        if (jsonText is null) return Fallback("no JSON block found");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonText); }
        catch (JsonException ex) { return Fallback($"JSON parse error: {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fallback("root is not an object");

            // Score
            if (!root.TryGetProperty("score", out var scoreEl) || scoreEl.ValueKind != JsonValueKind.Number)
            {
                return Fallback("missing or non-numeric 'score'");
            }
            var score = scoreEl.GetInt32();
            if (score < 0 || score > 100) return Fallback($"score {score} out of [0,100]");

            // Dims (optional but if present, each dim must be in [0, max])
            var dims = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("dims", out var dimsEl) && dimsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var (key, max) in Dims)
                {
                    if (dimsEl.TryGetProperty(key, out var dimEl) && dimEl.ValueKind == JsonValueKind.Number)
                    {
                        var v = dimEl.GetInt32();
                        if (v < 0 || v > max) return Fallback($"dim '{key}' value {v} out of [0,{max}]");
                        dims[key] = v;
                    }
                }
            }

            // Model-stated verdict (recorded for drift counter, not enforced).
            var modelVerdict = root.TryGetProperty("verdict", out var vEl) && vEl.ValueKind == JsonValueKind.String
                ? vEl.GetString()?.ToLowerInvariant()
                : null;

            // Notes — truncate at 800 chars per spec.
            var notes = root.TryGetProperty("notes", out var nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString() ?? string.Empty
                : string.Empty;
            if (notes.Length > MaxNotesChars) notes = notes.Substring(0, MaxNotesChars);

            var derived = DeriveVerdict(score);
            var mismatch = modelVerdict is not null && !string.Equals(modelVerdict, derived, StringComparison.OrdinalIgnoreCase);

            return new EvaluatorScore(
                Score: score,
                Dims: dims,
                Verdict: derived,
                ModelVerdict: modelVerdict,
                VerdictMismatch: mismatch,
                Notes: notes,
                ParseFailed: false,
                ParseError: null);
        }
    }

    /// <summary>
    /// Derive verdict from score per spec §2:
    /// <c>≥ 90 → pass; 70..89 → iterate; &lt; 70 → reject</c>.
    /// </summary>
    public static string DeriveVerdict(int score)
    {
        if (score >= ScoringConstants.PassThreshold)    return "pass";
        if (score >= ScoringConstants.IterateThreshold) return "iterate";
        return "reject";
    }

    private static EvaluatorScore Fallback(string reason) => new(
        Score: 0,
        Dims: new Dictionary<string, int>(),
        Verdict: "iterate",
        ModelVerdict: null,
        VerdictMismatch: false,
        Notes: string.Empty,
        ParseFailed: true,
        ParseError: reason);
}

/// <summary>
/// Phase 14.3 — structured evaluator output. Score + verdict are the
/// authoritative fields; <see cref="ModelVerdict"/> is a side-channel used
/// only to count drift between the model's claim and the math.
/// </summary>
public sealed record EvaluatorScore(
    int Score,
    IReadOnlyDictionary<string, int> Dims,
    string Verdict,
    string? ModelVerdict,
    bool VerdictMismatch,
    string Notes,
    bool ParseFailed,
    string? ParseError);
