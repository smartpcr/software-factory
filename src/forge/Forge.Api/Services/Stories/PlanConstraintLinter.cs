// -----------------------------------------------------------------------
// <copyright file="PlanConstraintLinter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Pure-helper linter for cross-stage <b>explicit constraints</b> in an
/// <c>implementation-plan.md</c>. Mirrors the
/// <see cref="E2eScenariosLinter"/> shape (in-process linter +
/// evaluator-prompt rules block) so it can run as a server-side gate
/// AND be quoted verbatim in the doc-evaluator prompt.
///
/// <para><b>What this catches.</b> An *explicit constraint* is any
/// quantified / bounded statement an upstream stage makes that a
/// downstream stage relies on — a migration range pin
/// (<c>0001..0034</c>), a closed-set assertion (<c>{backfill, delta}</c>),
/// a numeric cap, an "every <c>pri_*</c> knob" enumeration. When stages
/// later in the plan add items that fall inside such a constraint's
/// referent set, the constraint declaration MUST be updated; otherwise
/// the gate / consumer stage will reference a stale set and either
/// fail-open (silent skip) or fail-closed (drift the gate from reality).
/// </para>
///
/// <para><b>Why it exists.</b> 2026-06-04 case study — the
/// <c>code-intelligence:PR-INGESTER</c> story's
/// <c>implementation-plan.md</c> declares "Stage 6.4 applies migrations
/// <c>0001..0034</c>" while sibling stages (4.3 / 4.4 / 5.4 / 5.5)
/// introduce features that, on the actual disk, require migrations
/// 0035..0038. The plan-doc evaluator's vague "no contradictions
/// between sections" rule failed to catch the cross-stage broken promise
/// because each stage looked internally well-formed. The engineer
/// agent then loop-iterated for hours trying to satisfy two
/// mutually-exclusive plan clauses ("pin at 0034" AND
/// "run every <c>TestIntegration*</c>" — including tests that need
/// columns from 0035+) until either gave way. This linter makes the
/// drift mechanically catchable so the plan-doc evaluator can refuse
/// pass and the engineer-evaluator can escalate "stale plan
/// constraint" to the operator instead of iterating against a broken
/// contract.</para>
///
/// <para><b>Rules (v1).</b>
/// <list type="bullet">
/// <item><c>plan.range_pin_format</c> — a range pin uses non-4-digit
///   numbers or has lower &gt; upper.</item>
/// <item><c>plan.range_pin_inconsistent</c> — two stages in the same
///   doc declare pins with different upper bounds, with no documented
///   reason for the divergence.</item>
/// <item><c>plan.range_pin_below_explicit_max</c> — the doc explicitly
///   adds a migration file with number K (via
///   <c>migrations/KKKK_…sql</c>) but a range pin's upper bound is
///   &lt; K. The doc itself promises a migration the gate pin refuses
///   to apply.</item>
/// <item><c>plan.range_pin_below_workspace_head</c> (workspace-aware)
///   — the workspace contains a migration file on disk with number K
///   that no pin includes. Catches the case where the plan never
///   mentions the migration explicitly (so it can't be caught from
///   markdown alone) but the workstream's repo already has it because
///   a sibling story-stage merged it.</item>
/// </list>
/// </para>
///
/// <para><b>Scope (v1).</b> Migration range pins only. The same shape
/// generalises to enum closed-sets / config-knob enumerations; tracked
/// for v2 once the migration-pin case proves the model.</para>
/// </summary>
public static class PlanConstraintLinter
{
    /// <summary>Error code constants, mirroring the
    /// <see cref="E2eScenariosLinter.ErrorCodes"/> naming scheme so
    /// callers can branch on the literal string.</summary>
    public static class ErrorCodes
    {
        public const string RangePinFormat = "plan.range_pin_format";
        public const string RangePinInconsistent = "plan.range_pin_inconsistent";
        public const string RangePinBelowExplicitMax = "plan.range_pin_below_explicit_max";
        public const string RangePinBelowWorkspaceHead = "plan.range_pin_below_workspace_head";
    }

    /// <summary>One linter violation. <see cref="StageName"/> is the
    /// verbatim H2 heading of the offending stage (e.g. "Stage 6.4: Gate
    /// job pri-postgres"); <see cref="Detail"/> is a one-line
    /// operator-readable explanation that quotes the constraint and the
    /// counter-evidence side-by-side.</summary>
    public sealed record LintError(string Code, string StageName, string Detail);

    /// <summary>Result of running <see cref="Validate"/> over a doc.</summary>
    public sealed record LintResult(IReadOnlyList<LintError> Errors)
    {
        public bool IsClean => this.Errors.Count == 0;
        public static LintResult Clean { get; } = new(System.Array.Empty<LintError>());
    }

    // ── regex toolbox ────────────────────────────────────────────────────

    private static readonly Regex StageH2 = new(
        @"^##\s+Stage\s+(?<id>\d+\.\d+)\s*[:\-—]\s*(?<name>.+?)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex PhaseH1 = new(
        @"^#\s+Phase\s+\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Migration file path with a 4-digit numeric prefix.
    /// Captures the number so the linter can attribute "this stage
    /// explicitly adds migration NNNN" to the stage's body.</summary>
    private static readonly Regex MigrationFileAddition = new(
        @"migrations/(?<num>\d{4})_[A-Za-z0-9_]+\.sql",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Range pin of shape <c>NNNN..MMMM</c> — the canonical
    /// "apply migrations 0001..0034" form. Captures both bounds.</summary>
    private static readonly Regex RangePinDoubleDot = new(
        @"`?(?<lower>\d{4})\.\.(?<upper>\d{4})`?",
        RegexOptions.Compiled);

    /// <summary>Range pin in prose form, e.g. "migrations through
    /// <c>0034</c>" or "the migrations through `0034` ready". Captures
    /// the upper bound; the lower bound is implicitly <c>0001</c>.</summary>
    private static readonly Regex RangePinThrough = new(
        @"migrations?\s+through\s+`?(?<upper>\d{4})`?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Range pin in "NNNN through MMMM" form (Stage-1.1-style
    /// header text). Captures both bounds.</summary>
    private static readonly Regex RangePinThroughInclusive = new(
        @"(?<lower>\d{4})\s+through\s+(?<upper>\d{4})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Validate an <c>implementation-plan.md</c> markdown body. When
    /// <paramref name="workspacePath"/> is supplied the linter additionally
    /// cross-checks pin upper bounds against the highest migration file
    /// on disk under <c>{workspace}/services/**/migrations/NNNN_*.sql</c>
    /// (rule <c>plan.range_pin_below_workspace_head</c>). Pure /
    /// side-effect-free; safe to call from prompt-construction hot paths.
    /// </summary>
    public static LintResult Validate(string? markdown, string? workspacePath = null)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return LintResult.Clean;

        var normalised = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalised.Split('\n');
        var stages = ScanStages(lines);
        if (stages.Count == 0) return LintResult.Clean;

        var errors = new List<LintError>();
        var allPins = new List<(StageScan stage, RangePin pin)>();
        var allAdditions = new List<(StageScan stage, int migrationNumber)>();

        foreach (var stage in stages)
        {
            // Pre-pass: catalogue every explicit pin + every migration file
            // mention, attributing each to its containing stage. Format
            // errors (`plan.range_pin_format`) are emitted in the same
            // pass; downstream rules need only well-formed pins.
            foreach (var raw in EnumerateRangePins(stage))
            {
                if (raw.Lower < 1 || raw.Upper < raw.Lower)
                {
                    errors.Add(new LintError(
                        ErrorCodes.RangePinFormat,
                        stage.Heading,
                        $"{stage.Heading} declares range pin `{raw.RawText}` with malformed bounds "
                        + $"(lower={raw.Lower:D4}, upper={raw.Upper:D4}). Lower must be ≥ 0001 and ≤ upper."));
                    continue;
                }
                allPins.Add((stage, raw));
            }

            foreach (var num in EnumerateMigrationAdditions(stage))
            {
                allAdditions.Add((stage, num));
            }
        }

        // Rule: range_pin_below_explicit_max — every pin's upper bound
        // must cover every migration the doc itself adds. Catches a
        // gate-stage pin that lags behind a later-introduced migration
        // step elsewhere in the same plan (i.e. plan-internal drift).
        if (allAdditions.Count > 0)
        {
            var maxAddedNum = allAdditions.Max(a => a.migrationNumber);
            var maxAddedStage = allAdditions.First(a => a.migrationNumber == maxAddedNum).stage.Heading;
            foreach (var (pinStage, pin) in allPins)
            {
                if (pin.Upper < maxAddedNum)
                {
                    errors.Add(new LintError(
                        ErrorCodes.RangePinBelowExplicitMax,
                        pinStage.Heading,
                        $"{pinStage.Heading} declares range pin `{pin.RawText}` (upper={pin.Upper:D4}) "
                        + $"but the doc itself adds migration {maxAddedNum:D4} in {maxAddedStage}. "
                        + $"Bump the pin to ≥ {maxAddedNum:D4} or move the migration step into a "
                        + $"stage whose pin already covers it."));
                }
            }
        }

        // Rule: range_pin_inconsistent — two stages in the same doc
        // declare pins with different upper bounds. The lower bound is
        // expected to be `0001` in practice; only divergent uppers are
        // flagged. Picks the maximum as the de-facto truth and reports
        // every stragglers below it. (Two pins with the same upper are
        // fine — they're reinforcing the same constraint.)
        if (allPins.Count >= 2)
        {
            var maxPinUpper = allPins.Max(p => p.pin.Upper);
            var stragglerPins = allPins.Where(p => p.pin.Upper < maxPinUpper).ToList();
            if (stragglerPins.Count > 0)
            {
                var leadStage = allPins.First(p => p.pin.Upper == maxPinUpper).stage.Heading;
                foreach (var (sStage, sPin) in stragglerPins)
                {
                    errors.Add(new LintError(
                        ErrorCodes.RangePinInconsistent,
                        sStage.Heading,
                        $"{sStage.Heading} declares range pin `{sPin.RawText}` (upper={sPin.Upper:D4}) "
                        + $"but a sibling stage ({leadStage}) declares a pin reaching {maxPinUpper:D4}. "
                        + $"Both pins reference the same constraint; align them or document why they "
                        + $"intentionally diverge."));
                }
            }
        }

        // Rule (workspace-aware): range_pin_below_workspace_head — scan
        // the worktree for `services/**/migrations/NNNN_*.sql` files and
        // flag any pin whose upper bound is below the highest on disk.
        // This is the rule that catches the pri-postgres case: the plan
        // never explicitly mentions migrations 0035-0038 (so the pure-
        // markdown rules can't see them), but they exist on disk because
        // sibling stages of the same story added them.
        if (!string.IsNullOrWhiteSpace(workspacePath) && allPins.Count > 0)
        {
            var workspaceMax = ScanWorkspaceMigrationHead(workspacePath!);
            if (workspaceMax is int wsHead)
            {
                foreach (var (pinStage, pin) in allPins)
                {
                    if (pin.Upper < wsHead)
                    {
                        errors.Add(new LintError(
                            ErrorCodes.RangePinBelowWorkspaceHead,
                            pinStage.Heading,
                            $"{pinStage.Heading} declares range pin `{pin.RawText}` (upper={pin.Upper:D4}) "
                            + $"but the workspace's migrations directory already contains migration "
                            + $"{wsHead:D4}. The pin is stale relative to the story's current head — "
                            + $"either bump the pin (operator approval required for plan-doc changes) "
                            + $"or document why the new migrations are out-of-scope for this gate."));
                    }
                }
            }
        }

        return errors.Count == 0 ? LintResult.Clean : new LintResult(errors);
    }

    /// <summary>
    /// Render a prompt section listing the linter's hard requirements
    /// and (optionally) the current violations. Mirrors
    /// <see cref="E2eScenariosLinter.BuildEvaluatorRulesSection"/> so the
    /// LLM evaluator sees the same criteria the server-side gate
    /// enforces. Pass the validated <paramref name="result"/> to surface
    /// the live violations under a "Current violations" sub-section;
    /// omit it when building the rules block for an iter-1 prompt
    /// (file doesn't exist yet).
    /// </summary>
    public static string BuildEvaluatorRulesSection(LintResult? result = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## CROSS-STAGE CONSTRAINT CHECK — REFUSAL CRITERIA (PlanConstraintLinter)");
        sb.AppendLine();
        sb.AppendLine("An **explicit constraint** is any quantified / bounded statement an upstream");
        sb.AppendLine("stage makes that a downstream stage relies on — a migration range pin");
        sb.AppendLine("(`0001..0034`), a closed-set assertion (`{backfill, delta}`), a numeric cap,");
        sb.AppendLine("an \"every `pri_*` knob\" enumeration. When stages later in the plan add items");
        sb.AppendLine("that fall inside such a constraint's referent set, the constraint declaration");
        sb.AppendLine("MUST be updated. v1 of this check enforces it for **migration range pins**.");
        sb.AppendLine();
        sb.AppendLine("For EVERY range pin in this doc (shapes `NNNN..MMMM`, \"migrations through ");
        sb.AppendLine("`MMMM`\", or \"NNNN through MMMM\"):");
        sb.AppendLine($"- Bounds MUST be 4-digit, lower ≤ upper. Violation → `{ErrorCodes.RangePinFormat}`.");
        sb.AppendLine($"- Multiple pins in the same doc MUST agree on upper bound (or document the divergence).");
        sb.AppendLine($"  Violation → `{ErrorCodes.RangePinInconsistent}`.");
        sb.AppendLine($"- The pin's upper bound MUST be ≥ the highest migration number the doc itself");
        sb.AppendLine($"  adds via a `migrations/NNNN_<name>.sql` step. Violation →");
        sb.AppendLine($"  `{ErrorCodes.RangePinBelowExplicitMax}`. Practical example: Stage 6.4 declares");
        sb.AppendLine($"  pin `0001..0034` but Phase 1 of the same plan adds migrations through 0038 →");
        sb.AppendLine($"  the gate would silently skip the newer migrations.");
        sb.AppendLine($"- The pin's upper bound MUST be ≥ the highest migration file on disk under the");
        sb.AppendLine($"  workspace's `services/**/migrations/NNNN_*.sql`. Violation →");
        sb.AppendLine($"  `{ErrorCodes.RangePinBelowWorkspaceHead}`. This catches the case where");
        sb.AppendLine($"  earlier-merged sibling stages have moved the story's head past the pin");
        sb.AppendLine($"  WITHOUT the plan text being updated — the bug class that motivates this linter.");
        sb.AppendLine();
        sb.AppendLine("### Algorithm — apply this every iteration");
        sb.AppendLine("1. Extract every range pin in the doc (regex above).");
        sb.AppendLine("2. Extract every migration file mention (`migrations/NNNN_*.sql`).");
        sb.AppendLine("3. List `services/*/migrations/*.sql` from the worktree and take `max(NNNN)`.");
        sb.AppendLine("4. For each pin, verify it satisfies all four rules. Any failure → add a");
        sb.AppendLine("   checkbox to `What still needs work` quoting the pin verbatim AND the");
        sb.AppendLine("   counter-evidence (the higher migration number you found).");
        sb.AppendLine();
        sb.AppendLine("You MUST return `verdict: \"iterate\"` (or `reject` if iter ≥ 3) when ANY range");
        sb.AppendLine("pin violates a rule, regardless of how good the doc looks on other dimensions.");
        sb.AppendLine("Score MUST be < 90 until every violation is resolved.");
        sb.AppendLine();
        sb.AppendLine("### When the constraint is genuinely stale (plan needs updating)");
        sb.AppendLine("If the impl plan's constraint is provably wrong against reality (e.g. the");
        sb.AppendLine("workspace's `migrations/` head is `0038` but the pin says `0034`), flag");
        sb.AppendLine("`plan.range_pin_below_workspace_head` AND surface an Open Question telling the");
        sb.AppendLine("operator the plan must be updated by an operator-approved edit before this");
        sb.AppendLine("stage can pass. Do NOT silently rewrite the constraint in the doc.");
        if (result is not null && !result.IsClean)
        {
            sb.AppendLine();
            sb.AppendLine("### Current violations (live PlanConstraintLinter pass)");
            foreach (var err in result.Errors)
            {
                sb.AppendLine($"- `{err.Code}` — {err.Detail}");
            }
        }
        return sb.ToString();
    }

    // ── internal scan ────────────────────────────────────────────────────

    private sealed record StageScan(string Heading, string Body);

    private sealed record RangePin(int Lower, int Upper, string RawText);

    private static IReadOnlyList<StageScan> ScanStages(string[] lines)
    {
        var stages = new List<StageScan>();
        string? currentHeading = null;
        var body = new StringBuilder();

        void Flush()
        {
            if (currentHeading is null) return;
            stages.Add(new StageScan(currentHeading, body.ToString()));
            body.Clear();
        }

        foreach (var line in lines)
        {
            var stageMatch = StageH2.Match(line);
            if (stageMatch.Success)
            {
                Flush();
                currentHeading = $"Stage {stageMatch.Groups["id"].Value}: {stageMatch.Groups["name"].Value}";
                continue;
            }

            // A Phase H1 / different H2 closes the current stage but
            // does not open a new one. Stage scope ends at the next
            // structural heading.
            if (PhaseH1.IsMatch(line) || (line.StartsWith("## ", System.StringComparison.Ordinal) && !stageMatch.Success))
            {
                Flush();
                currentHeading = null;
                continue;
            }

            if (currentHeading is not null)
            {
                body.AppendLine(line);
            }
        }

        Flush();
        return stages;
    }

    private static IEnumerable<RangePin> EnumerateRangePins(StageScan stage)
    {
        // Order matters: try the most specific shape first so a header
        // like "Operational tables migrations 0031 through 0034" is not
        // double-counted as two prose mentions. Scan BOTH the heading
        // and the body — Stage headings frequently encode the range
        // (e.g. `## Stage 1.3: … migrations 0031 through 0034`).
        var seenSpans = new HashSet<string>();
        var haystack = stage.Heading + "\n" + stage.Body;

        foreach (Match m in RangePinDoubleDot.Matches(haystack))
        {
            if (TryParseFourDigit(m.Groups["lower"].Value, out var lo)
                && TryParseFourDigit(m.Groups["upper"].Value, out var hi))
            {
                var key = $"dd:{lo}:{hi}";
                if (seenSpans.Add(key))
                {
                    yield return new RangePin(lo, hi, m.Value);
                }
            }
        }

        foreach (Match m in RangePinThroughInclusive.Matches(haystack))
        {
            if (TryParseFourDigit(m.Groups["lower"].Value, out var lo)
                && TryParseFourDigit(m.Groups["upper"].Value, out var hi))
            {
                var key = $"th:{lo}:{hi}";
                if (seenSpans.Add(key))
                {
                    yield return new RangePin(lo, hi, m.Value);
                }
            }
        }

        foreach (Match m in RangePinThrough.Matches(haystack))
        {
            if (TryParseFourDigit(m.Groups["upper"].Value, out var hi))
            {
                var key = $"thr:{hi}";
                if (seenSpans.Add(key))
                {
                    yield return new RangePin(1, hi, m.Value);
                }
            }
        }
    }

    private static IEnumerable<int> EnumerateMigrationAdditions(StageScan stage)
    {
        foreach (Match m in MigrationFileAddition.Matches(stage.Body))
        {
            if (TryParseFourDigit(m.Groups["num"].Value, out var n))
            {
                yield return n;
            }
        }
    }

    private static bool TryParseFourDigit(string s, out int value)
    {
        value = 0;
        if (s.Length != 4) return false;
        foreach (var c in s)
        {
            if (c < '0' || c > '9') return false;
        }
        value = int.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// Walk <c>{workspace}/services/**/migrations/NNNN_*.sql</c> and
    /// return the highest <c>NNNN</c> on disk, or <c>null</c> when the
    /// workspace has no such tree. Best-effort: any IO error returns
    /// <c>null</c> so the linter degrades to the pure-markdown rules
    /// rather than throwing on a missing / unreadable worktree.
    /// </summary>
    private static int? ScanWorkspaceMigrationHead(string workspacePath)
    {
        try
        {
            var servicesRoot = Path.Combine(workspacePath, "services");
            if (!Directory.Exists(servicesRoot))
            {
                // Fallback: some repos keep migrations at the root or
                // under a different convention. Scan the whole worktree
                // for `migrations/NNNN_*.sql` paths, capped at a sane
                // depth so we don't walk node_modules / .git equivalents.
                return ScanWorkspaceMigrationHeadFallback(workspacePath);
            }

            var max = -1;
            foreach (var dir in Directory.EnumerateDirectories(servicesRoot))
            {
                var migDir = Path.Combine(dir, "migrations");
                if (!Directory.Exists(migDir)) continue;
                foreach (var file in Directory.EnumerateFiles(migDir, "*.sql", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(file);
                    if (name.Length >= 4 && TryParseFourDigit(name[..4], out var n) && n > max)
                    {
                        max = n;
                    }
                }
            }
            return max >= 0 ? max : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? ScanWorkspaceMigrationHeadFallback(string workspacePath)
    {
        try
        {
            // Hand-walk so we can skip `.git` / `node_modules` /
            // `.worktree` cheaply without `EnumerateFiles(*, recursive)`
            // visiting those subtrees first.
            var max = -1;
            var queue = new Queue<string>();
            queue.Enqueue(workspacePath);
            var visited = 0;
            const int Cap = 5000; // Bounded walk; bail rather than rescan a giant monorepo.
            while (queue.Count > 0 && visited < Cap)
            {
                var dir = queue.Dequeue();
                visited++;
                string[] subs;
                try { subs = Directory.GetDirectories(dir); } catch { continue; }
                foreach (var sub in subs)
                {
                    var leaf = Path.GetFileName(sub);
                    if (leaf is ".git" or "node_modules" or ".worktree" or "bin" or "obj") continue;
                    if (string.Equals(leaf, "migrations", System.StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var file in Directory.EnumerateFiles(sub, "*.sql", SearchOption.TopDirectoryOnly))
                        {
                            var name = Path.GetFileName(file);
                            if (name.Length >= 4 && TryParseFourDigit(name[..4], out var n) && n > max)
                            {
                                max = n;
                            }
                        }
                    }
                    queue.Enqueue(sub);
                }
            }
            return max >= 0 ? max : null;
        }
        catch
        {
            return null;
        }
    }
}
