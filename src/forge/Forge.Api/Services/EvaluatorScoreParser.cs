// -----------------------------------------------------------------------
// <copyright file="EvaluatorScoreParser.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text;
using System.Text.Json;

/// <summary>
/// Phase 14.3 — pure parser for an evaluator's last assistant message.
/// The locked JSON shape is documented in the spec at §2:
/// <code>
/// {
///   "score": 0..100,
///   "dims": { "correctness": 0..40, "quality": 0..25,
///             "tests": 0..15, "scope": 0..10, "risk": 0..10 },
///   "verdict": "pass" | "iterate" | "reject",
///   "notes": "&lt;= 4000 chars"
/// }
/// </code>
/// <para>
/// The model returns this inside a fenced <c>```json</c> block; we extract the
/// LATEST plausible JSON candidate, parse it strictly first, and on failure
/// apply a conservative cleanup pass (strip JS-style comments / trailing
/// commas, normalize known unquoted keys, single→double quotes when safe)
/// and retry. If both parses fail we fall back to a deterministic
/// <c>iterate</c>-with-score-0 result and set <see cref="EvaluatorScore.ParseFailed"/>.
/// </para>
/// <para>
/// Verdict is **derived** from score (<see cref="ScoringConstants.PassThreshold"/>
/// / <see cref="ScoringConstants.IterateThreshold"/>) — the model's stated
/// verdict is recorded in <see cref="EvaluatorScore.ModelVerdict"/> for the
/// drift counter but never overrides the math.
/// </para>
/// <para>
/// 2026-05-06 leniency upgrade: replaces a two-regex first-match pipeline
/// that picked the FIRST <c>{…}</c> in the message — a real bug when the
/// evaluator quoted code earlier in its prose. New strategy is "latest
/// candidate wins" via a depth-tracked scan that respects string and
/// comment state, plus a single-pass JSON5-style cleanup. Live data showed
/// 13 of 73 deliverables on a single workstream burning iteration slots
/// because the model wrote unquoted keys (<c>'r'/'i' is an invalid start of
/// a property name</c>) or skipped the JSON fence entirely.
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

    /// <summary>
    /// Schema-aware allowlist for unquoted-key repair. Per rubber-duck
    /// critique #7: known-key repair is safer than general JS-object repair
    /// because we never touch unfamiliar identifiers. Includes aliases that
    /// the alias resolver maps onto <c>notes</c> (critique #2).
    /// </summary>
    private static readonly HashSet<string> RepairableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "score", "dims", "correctness", "quality", "tests", "scope",
        "risk", "verdict", "notes", "reasoning", "feedback", "review",
    };

    /// <summary>
    /// Aliases checked when <c>notes</c> is missing or empty (critique #2 —
    /// otherwise a recovered <c>{ score: 86, reasoning: "…" }</c> would
    /// silently produce empty Notes and the next-iteration generator would
    /// get no actionable feedback).
    /// </summary>
    private static readonly string[] NotesAliases = { "notes", "reasoning", "feedback", "review" };

    // Bumped from 800 → 4000 (2026-05-03): the prior cap chopped feedback
    // mid-sentence, so the generator never received the actionable part of
    // the review. WI-1035 stalled at score 10 because every iteration's
    // notes ended at "FailurePattern records still default to…" without
    // ever stating the fix. 4000 is generous enough to fit the rubric
    // breakdown + a multi-paragraph fix recipe; the deliverables row stays
    // narrow because we don't index this column.
    private const int MaxNotesChars = 4000;

    private const string LenientRecoveryPrefix = "[parsed via lenient recovery] ";

    /// <summary>
    /// Parse an evaluator's assistant text into a structured score. Always
    /// returns a non-null result; <see cref="EvaluatorScore.ParseFailed"/>
    /// is true when the input was malformed.
    /// </summary>
    public static EvaluatorScore Parse(string? assistantText, int workItemPoints = 1)
    {
        if (string.IsNullOrWhiteSpace(assistantText)) return Fallback("empty assistant text");

        var jsonText = SelectLatestCandidate(assistantText);
        if (jsonText is null) return Fallback("no JSON block found");

        // Strict first.
        if (TryParseAndBuild(jsonText, workItemPoints, recovered: false) is { } strict)
        {
            return strict;
        }

        // Lenient retry — apply cleanup ONCE on the candidate and reparse.
        var cleaned = CleanupJsonish(jsonText);
        if (!ReferenceEquals(cleaned, jsonText)
            && TryParseAndBuild(cleaned, workItemPoints, recovered: true) is { } lenient)
        {
            return lenient;
        }

        // Both attempts failed — surface the strict-parse error so the
        // next iteration's generator can see what shape the model emitted.
        try { JsonDocument.Parse(jsonText); }
        catch (JsonException ex) { return Fallback($"JSON parse error: {ex.Message}"); }
        return Fallback("JSON did not match the expected shape");
    }

    /// <summary>
    /// Derive verdict from score, adjusted by work-item points:
    /// <c>≥ 90 → pass; [reject..89] → iterate; &lt; reject → reject</c>.
    /// The reject threshold decreases for higher-point (harder) items.
    /// </summary>
    public static string DeriveVerdict(int score, int workItemPoints = 1)
    {
        if (score >= ScoringConstants.PassThreshold) return "pass";
        // Score 0 always iterates — it means the generator failed to
        // produce the file (timeout, tool error, etc.), not that the
        // work quality is so poor it should be permanently rejected.
        if (score == 0) return "iterate";
        if (score >= ScoringConstants.RejectThreshold(workItemPoints)) return "iterate";
        return "reject";
    }

    /// <summary>
    /// Try to parse a candidate JSON string and build a fully validated
    /// <see cref="EvaluatorScore"/>. Returns null when parsing fails or the
    /// shape is wrong (caller decides whether to apply cleanup or fall back).
    /// </summary>
    private static EvaluatorScore? TryParseAndBuild(string jsonText, int workItemPoints, bool recovered)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonText); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // Score (required, numeric, in range)
            if (!root.TryGetProperty("score", out var scoreEl) || scoreEl.ValueKind != JsonValueKind.Number)
            {
                return null;
            }
            if (!scoreEl.TryGetInt32(out var score)) return null;
            if (score < 0 || score > 100) return null;

            // Dims (optional; each present dim must be in [0, max])
            var dims = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("dims", out var dimsEl) && dimsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var (key, max) in Dims)
                {
                    if (dimsEl.TryGetProperty(key, out var dimEl) && dimEl.ValueKind == JsonValueKind.Number)
                    {
                        if (!dimEl.TryGetInt32(out var v)) return null;
                        if (v < 0 || v > max) return null;
                        dims[key] = v;
                    }
                }
            }

            // Model-stated verdict (recorded for drift counter, not enforced).
            var modelVerdict = root.TryGetProperty("verdict", out var vEl) && vEl.ValueKind == JsonValueKind.String
                ? vEl.GetString()?.ToLowerInvariant()
                : null;

            // Notes — try canonical key first, then aliases (critique #2).
            var notes = string.Empty;
            foreach (var alias in NotesAliases)
            {
                if (root.TryGetProperty(alias, out var nEl) && nEl.ValueKind == JsonValueKind.String)
                {
                    var s = nEl.GetString();
                    if (!string.IsNullOrEmpty(s)) { notes = s; break; }
                }
            }
            if (notes.Length > MaxNotesChars) notes = notes.Substring(0, MaxNotesChars);

            // Recovered parses get a small Notes prefix so operators can spot
            // model drift even before the deferred forge.eval.parse_failed
            // counter lands (Phase 14.12).
            if (recovered)
            {
                notes = LenientRecoveryPrefix
                    + (string.IsNullOrEmpty(notes) ? "(no notes provided)" : notes);
                if (notes.Length > MaxNotesChars) notes = notes.Substring(0, MaxNotesChars);
            }

            var derived = DeriveVerdict(score, workItemPoints);
            var mismatch = modelVerdict is not null && !string.Equals(modelVerdict, derived, StringComparison.OrdinalIgnoreCase);

            return new EvaluatorScore(
                Score: score,
                Dims: dims,
                Verdict: derived,
                ModelVerdict: modelVerdict,
                VerdictMismatch: mismatch,
                Notes: notes,
                ParseFailed: false,
                ParseError: null,
                ParseRecovered: recovered);
        }
    }

    private static EvaluatorScore Fallback(string reason) => new(
        Score: 0,
        Dims: new Dictionary<string, int>(),
        Verdict: "iterate",
        ModelVerdict: null,
        VerdictMismatch: false,
        // Surface the parse error in Notes so the next iteration's generator
        // sees *why* feedback was empty. Without this, the generator gets
        // `(no notes)` in its prior-chain, panics, and triggers the "try a
        // fundamentally different strategy" branch — destroying good work
        // because the evaluator stuttered. Marking it explicitly tells the
        // generator: evaluator pipeline fault, NOT a work-quality signal.
        Notes: $"[evaluator output could not be parsed: {reason}] "
            + "This is a pipeline fault, not feedback on your work. "
            + "Do NOT rewrite your prior work in response. Re-run your "
            + "build / tests once and wait for the next evaluator turn.",
        ParseFailed: true,
        ParseError: reason,
        ParseRecovered: false);

    // ───────────────────────────────────────────────────────────────────
    // Candidate selection
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pick the JSON candidate with the LATEST start position in the
    /// assistant text (critique #1). Considers both fenced blocks and
    /// balanced bare top-level <c>{…}</c> objects; both kinds are weighed by
    /// position alone so a later bare answer beats an earlier example block.
    /// String and comment state is tracked so braces inside strings or
    /// comments don't poison selection (critique #3).
    /// </summary>
    private static string? SelectLatestCandidate(string text)
    {
        string? best = null;
        var bestStart = -1;

        // Pass 1 — fenced ```json or unlabeled ``` whose body starts with `{`.
        var fenceIdx = 0;
        while (fenceIdx < text.Length)
        {
            var open = text.IndexOf("```", fenceIdx, StringComparison.Ordinal);
            if (open < 0) break;
            // Skip language tag (json / JSON / nothing) up to newline.
            var bodyStart = open + 3;
            while (bodyStart < text.Length && text[bodyStart] != '\n' && text[bodyStart] != '\r')
            {
                bodyStart++;
            }
            // Skip the newline(s) terminating the info string.
            while (bodyStart < text.Length && (text[bodyStart] == '\n' || text[bodyStart] == '\r'))
            {
                bodyStart++;
            }
            var close = text.IndexOf("```", bodyStart, StringComparison.Ordinal);
            if (close < 0) break;

            var body = text[bodyStart..close].Trim();
            if (body.Length > 0 && body[0] == '{')
            {
                if (open > bestStart) { best = body; bestStart = open; }
            }
            fenceIdx = close + 3;
        }

        // Pass 2 — every balanced top-level {…} where the opening `{` is in
        // the Code state of the lexer. Pick the latest start position.
        var lex = new JsonishLexer(text);
        var stack = new Stack<int>();
        while (lex.Read(out var ch, out var state, out var pos))
        {
            if (state != LexState.Code) continue;
            if (ch == '{') stack.Push(pos);
            else if (ch == '}' && stack.Count > 0)
            {
                var openPos = stack.Pop();
                if (stack.Count == 0)
                {
                    if (openPos > bestStart)
                    {
                        best = text[openPos..(pos + 1)];
                        bestStart = openPos;
                    }
                }
            }
        }

        return best;
    }

    // ───────────────────────────────────────────────────────────────────
    // Cleanup (JSON5-ish → strict JSON)
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Apply conservative cleanup on a candidate substring. Strips line/
    /// block comments, drops trailing commas, converts safe single-quoted
    /// strings to double-quoted, and quotes a known allowlist of
    /// unquoted JSON keys. Hand-rolled state machine — never touches
    /// content inside a string literal. Returns the original string
    /// unchanged when no transformation was needed (callers can then
    /// short-circuit the retry).
    /// </summary>
    private static string CleanupJsonish(string src)
    {
        var sb = new StringBuilder(src.Length);
        var changed = false;
        var i = 0;
        while (i < src.Length)
        {
            var c = src[i];

            // Block comment /* … */ — drop entirely.
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                var end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? src.Length : end + 2;
                changed = true;
                continue;
            }
            // Line comment // … — drop entirely (keep the newline so line-
            // count stays sane in any error messages that show offsets).
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                var nl = src.IndexOf('\n', i + 2);
                i = nl < 0 ? src.Length : nl;
                changed = true;
                continue;
            }
            // Double-quoted string — emit verbatim (escapes preserved).
            if (c == '"')
            {
                sb.Append(c);
                i++;
                while (i < src.Length)
                {
                    var d = src[i];
                    sb.Append(d);
                    i++;
                    if (d == '\\' && i < src.Length) { sb.Append(src[i]); i++; continue; }
                    if (d == '"') break;
                }
                continue;
            }
            // Single-quoted string — convert to double-quoted when safe.
            if (c == '\'')
            {
                var bodyStart = i + 1;
                var j = bodyStart;
                var safe = true;
                while (j < src.Length)
                {
                    var d = src[j];
                    if (d == '\\') { safe = false; j = j + 1 < src.Length ? j + 2 : src.Length; continue; }
                    if (d == '\'') break;
                    // Conservative per critique #6 — refuse to mutate runs
                    // containing a double quote (would corrupt on rewrap)
                    // or raw control chars (not valid JSON-string content).
                    if (d == '"' || d == '\n' || d == '\r' || d < 0x20) safe = false;
                    j++;
                }
                if (j >= src.Length)
                {
                    // Unterminated single-quoted run — leave verbatim. The
                    // strict-parse retry will fail predictably and we'll
                    // fall back rather than corrupt the body.
                    sb.Append(src, i, src.Length - i);
                    i = src.Length;
                    continue;
                }
                var body = src[bodyStart..j];
                if (safe)
                {
                    sb.Append('"').Append(body).Append('"');
                    changed = true;
                }
                else
                {
                    sb.Append('\'').Append(body).Append('\'');
                }
                i = j + 1;
                continue;
            }
            // Trailing-comma scrub: when we hit `}` or `]` in code state,
            // walk back over Code-only whitespace and drop any trailing `,`.
            if (c == '}' || c == ']')
            {
                if (TryDropTrailingComma(sb)) changed = true;
                sb.Append(c);
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }

        var afterStringsAndCommentsAndCommas = sb.ToString();
        var keysQuoted = QuoteKnownUnquotedKeys(afterStringsAndCommentsAndCommas, out var anyKeyQuoted);
        if (anyKeyQuoted) changed = true;

        return changed ? keysQuoted : src;
    }

    private static bool TryDropTrailingComma(StringBuilder sb)
    {
        var i = sb.Length - 1;
        while (i >= 0 && (sb[i] == ' ' || sb[i] == '\t' || sb[i] == '\r' || sb[i] == '\n'))
        {
            i--;
        }
        if (i >= 0 && sb[i] == ',')
        {
            sb.Length = i;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Scan for unquoted object keys (identifier from the
    /// <see cref="RepairableKeys"/> allowlist followed by <c>:</c>) and
    /// wrap them in double quotes. State-aware so we never modify content
    /// inside a string literal. Comments are already gone by this point
    /// (the prior cleanup pass strips them), so we only need to track
    /// double-quoted and surviving single-quoted runs.
    /// </summary>
    private static string QuoteKnownUnquotedKeys(string src, out bool anyChange)
    {
        anyChange = false;
        var sb = new StringBuilder(src.Length);
        var i = 0;
        while (i < src.Length)
        {
            var c = src[i];

            // Skip double-quoted strings verbatim.
            if (c == '"')
            {
                sb.Append(c);
                i++;
                while (i < src.Length)
                {
                    var d = src[i];
                    sb.Append(d);
                    i++;
                    if (d == '\\' && i < src.Length) { sb.Append(src[i]); i++; continue; }
                    if (d == '"') break;
                }
                continue;
            }
            // Skip surviving single-quoted strings verbatim (cleanup left
            // unsafe runs as-is so the parse will fail safely).
            if (c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < src.Length)
                {
                    var d = src[i];
                    sb.Append(d);
                    i++;
                    if (d == '\\' && i < src.Length) { sb.Append(src[i]); i++; continue; }
                    if (d == '\'') break;
                }
                continue;
            }

            // Unquoted-key candidate: must follow `{` or `,` (with optional
            // whitespace between) and be in the allowlist.
            if (IsIdentStart(c))
            {
                var prev = LastNonWhitespaceBefore(sb);
                if (prev == '{' || prev == ',')
                {
                    var start = i;
                    while (i < src.Length && IsIdentChar(src[i])) i++;
                    var ident = src[start..i];
                    var k = i;
                    while (k < src.Length && (src[k] == ' ' || src[k] == '\t' || src[k] == '\r' || src[k] == '\n')) k++;
                    if (k < src.Length && src[k] == ':' && RepairableKeys.Contains(ident))
                    {
                        sb.Append('"').Append(ident).Append('"');
                        anyChange = true;
                        continue;
                    }
                    sb.Append(ident);
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static char LastNonWhitespaceBefore(StringBuilder sb)
    {
        for (var i = sb.Length - 1; i >= 0; i--)
        {
            var c = sb[i];
            if (c != ' ' && c != '\t' && c != '\r' && c != '\n') return c;
        }
        return '\0';
    }

    private static bool IsIdentStart(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';

    private static bool IsIdentChar(char c) =>
        IsIdentStart(c) || (c >= '0' && c <= '9') || c == '-';
}

/// <summary>
/// Phase 14.3 — structured evaluator output. Score + verdict are the
/// authoritative fields; <see cref="ModelVerdict"/> is a side-channel used
/// only to count drift between the model's claim and the math.
/// <see cref="ParseRecovered"/> is true when the parse only succeeded after
/// the lenient cleanup pass — distinguishes strict pass (clean evaluator
/// output) from repaired pass (model drift) without changing the verdict
/// math.
/// </summary>
public sealed record EvaluatorScore(
    int Score,
    IReadOnlyDictionary<string, int> Dims,
    string Verdict,
    string? ModelVerdict,
    bool VerdictMismatch,
    string Notes,
    bool ParseFailed,
    string? ParseError,
    bool ParseRecovered = false);

/// <summary>
/// Lexer-tracked syntactic state at a given character position.
/// <c>SingleQuoted</c> is intentionally absent — see <see cref="JsonishLexer"/>
/// for why apostrophes in prose make tracking single-quoted strings during
/// candidate selection fragile.
/// </summary>
internal enum LexState
{
    Code,
    DoubleQuoted,
    LineComment,
    BlockComment,
}

/// <summary>
/// Forward-only lexer over a JSON-ish string. Tracks double-quoted strings
/// and JS-style comments so candidate selection never confuses content for
/// syntax — a `{` inside a string or comment must NOT count as the start of
/// a top-level object (rubber-duck critique #3).
/// <para>
/// We deliberately do NOT track single-quoted strings here because real
/// evaluator prose contains stray apostrophes (e.g., "the agent's review")
/// that would otherwise put the lexer into a single-quoted state for the
/// entire rest of the message — masking every subsequent `{...}` candidate.
/// JSON5-style single-quoted string handling is the cleanup pass's job;
/// the candidate selector uses depth tracking on `{`/`}` plus double-quote
/// state, which is sufficient for the "latest candidate wins" contract.
/// </para>
/// </summary>
internal sealed class JsonishLexer
{
    private readonly string src;
    private int idx;
    private LexState state = LexState.Code;

    public JsonishLexer(string src) { this.src = src; }

    /// <summary>
    /// Read the next character. Returns false at end-of-input. <paramref
    /// name="reportedState"/> is the lexer state the character belongs to —
    /// the opening/closing delimiter of a string or comment is reported as
    /// <see cref="LexState.Code"/>; the body characters are reported as
    /// the corresponding string/comment state.
    /// </summary>
    public bool Read(out char ch, out LexState reportedState, out int pos)
    {
        if (this.idx >= this.src.Length)
        {
            ch = '\0'; reportedState = this.state; pos = this.idx; return false;
        }

        pos = this.idx;
        ch = this.src[this.idx];
        var s = this.src;

        switch (this.state)
        {
            case LexState.Code:
                if (ch == '"') { this.state = LexState.DoubleQuoted; reportedState = LexState.Code; this.idx++; return true; }
                if (ch == '/' && this.idx + 1 < s.Length)
                {
                    if (s[this.idx + 1] == '/') { this.state = LexState.LineComment; reportedState = LexState.Code; this.idx++; return true; }
                    if (s[this.idx + 1] == '*') { this.state = LexState.BlockComment; reportedState = LexState.Code; this.idx++; return true; }
                }
                reportedState = LexState.Code;
                this.idx++;
                return true;

            case LexState.DoubleQuoted:
                if (ch == '\\' && this.idx + 1 < s.Length)
                {
                    reportedState = LexState.DoubleQuoted;
                    this.idx++;
                    return true;
                }
                if (ch == '"') { this.state = LexState.Code; reportedState = LexState.Code; this.idx++; return true; }
                reportedState = LexState.DoubleQuoted;
                this.idx++;
                return true;

            case LexState.LineComment:
                if (ch == '\n') { this.state = LexState.Code; reportedState = LexState.LineComment; this.idx++; return true; }
                reportedState = LexState.LineComment;
                this.idx++;
                return true;

            case LexState.BlockComment:
                if (ch == '*' && this.idx + 1 < s.Length && s[this.idx + 1] == '/')
                {
                    reportedState = LexState.BlockComment;
                    this.idx += 2;
                    this.state = LexState.Code;
                    return true;
                }
                reportedState = LexState.BlockComment;
                this.idx++;
                return true;
        }

        reportedState = this.state;
        this.idx++;
        return true;
    }
}
