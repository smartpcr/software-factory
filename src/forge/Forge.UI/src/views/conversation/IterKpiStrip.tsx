// Insight I.3 (docs/forge/workstream-insights.md) — per-iter KPI
// chip strip + right-edge sparkline column. Sits above the tab nav
// in the Conversation view so the operator can see score / token /
// cost / files / duration progression at a glance and click an iter
// to filter the view to it.

import { useMemo } from 'react';
import { IterSparkline } from './IterSparkline';

export interface IterKpi {
  iter: number;
  score: number;
  verdict: string;
  inputTokens: number;
  outputTokens: number;
  costUsd: number;
  durationMs: number | null;
  filesChanged: number;
  createdAt: string;
  // 2026-05-16 Phase 72 — prompt-cache + reasoning + conversation
  // token telemetry from the Copilot SDK. Null on iters that ran
  // against a backend (Claude SDK / Copilot CLI / pre-Phase-72 rows)
  // that doesn't surface these fields.
  cacheReadTokens?: number | null;
  cacheWriteTokens?: number | null;
  reasoningTokens?: number | null;
  conversationTokens?: number | null;
}

interface Props {
  iters: IterKpi[];
  activeIter: number | null;
  onSelectIter?: (iter: number) => void;
}

export function IterKpiStrip({ iters, activeIter, onSelectIter }: Props): JSX.Element | null {
  if (iters.length === 0) {
    // Placeholder strip — keeps the visual structure consistent so the
    // operator sees the sparkline area even before the gen↔eval loop
    // produces its first deliverable.
    return (
      <div
        className="iter-kpi-strip"
        role="region"
        aria-label="Iteration KPIs"
        style={{
          display: 'flex',
          gap: 8,
          padding: '8px 10px',
          borderBottom: '1px solid var(--border, #2a2f3a)',
          background: 'var(--bg-elevated, rgba(255,255,255,0.02))',
          fontFamily: 'var(--sc-font-mono, monospace)',
          overflowX: 'auto',
        }}
      >
        <div style={{ flex: 1, display: 'flex', alignItems: 'center', fontSize: 10, color: 'var(--text-tertiary, #8d94a3)', letterSpacing: '0.08em' }}>
          No iterations yet — KPI chips and sparklines appear after the first gen↔eval pass.
        </div>
        <div
          style={{
            display: 'grid',
            gridTemplateRows: 'repeat(5, 1fr)',
            gap: 1,
            alignItems: 'center',
            fontSize: 10,
            color: 'var(--text-tertiary, #8d94a3)',
            minWidth: 130,
          }}
        >
          <SparkRow label="score" values={[]} stroke="var(--sc-cyan, #5eead4)" />
          <SparkRow label="tok"   values={[]} stroke="var(--sc-amber, #ffb347)" />
          <SparkRow label="cost"  values={[]} stroke="var(--sc-green, #6ee7b7)" />
          <SparkRow label="files" values={[]} stroke="var(--sc-purple, #c4b5fd)" />
          <SparkRow label="dur"   values={[]} stroke="var(--text-tertiary, #8d94a3)" />
        </div>
      </div>
    );
  }

  const scores = useMemo(() => iters.map((i) => i.score), [iters]);
  const totalTokens = useMemo(() => iters.map((i) => i.inputTokens + i.outputTokens), [iters]);
  const costs = useMemo(() => iters.map((i) => i.costUsd), [iters]);
  const files = useMemo(() => iters.map((i) => i.filesChanged), [iters]);
  const durations = useMemo(() => iters.map((i) => i.durationMs ?? 0), [iters]);
  // 2026-05-16 Phase 72 — cache-hit percentage per iter for the
  // sparkline column. Hit rate = cache_read / (cache_read + fresh_input)
  // so 100% means the entire prompt prefix was served from cache.
  // Iters with no cache data report 0 so the sparkline can still draw
  // a continuous shape (the chip-side render uses null to suppress
  // the badge).
  const cachePcts = useMemo(() => iters.map((i) => cacheHitPercent(i) ?? 0), [iters]);
  const hasAnyCacheData = useMemo(() => iters.some((i) => cacheHitPercent(i) !== null), [iters]);

  return (
    <div
      className="iter-kpi-strip"
      role="region"
      aria-label="Iteration KPIs"
      style={{
        display: 'flex',
        gap: 8,
        padding: '8px 10px',
        borderBottom: '1px solid var(--border, #2a2f3a)',
        background: 'var(--bg-elevated, rgba(255,255,255,0.02))',
        fontFamily: 'var(--sc-font-mono, monospace)',
        overflowX: 'auto',
      }}
    >
      {/* Chip strip */}
      <div style={{ display: 'flex', gap: 4, flex: 1 }}>
        {iters.map((it) => {
          const isActive = activeIter === it.iter;
          const cachePct = cacheHitPercent(it);
          const convTok = it.conversationTokens ?? null;
          return (
            <button
              key={it.iter}
              type="button"
              onClick={() => onSelectIter?.(it.iter)}
              title={buildChipTitle(it)}
              style={{
                display: 'grid',
                gridTemplateRows: 'repeat(6, 1fr)',
                gap: 1,
                padding: '4px 6px',
                minWidth: 56,
                fontSize: 10,
                lineHeight: '12px',
                textAlign: 'right',
                background: isActive
                  ? 'var(--sc-cyan-bg, rgba(94,234,212,0.12))'
                  : 'transparent',
                border: `1px solid ${
                  isActive
                    ? 'var(--sc-cyan, #5eead4)'
                    : 'var(--border, #2a2f3a)'
                }`,
                color: 'var(--text-primary, #e3e6eb)',
                cursor: onSelectIter ? 'pointer' : 'default',
                fontFamily: 'inherit',
                borderRadius: 2,
              }}
            >
              <div style={{ fontWeight: 700, color: scoreColor(it.score, it.verdict) }}>
                {it.score}
              </div>
              <div style={{ color: 'var(--text-secondary, #8d94a3)' }}>
                {formatTokens(it.inputTokens + it.outputTokens)}
              </div>
              <div style={{ color: 'var(--text-secondary, #8d94a3)' }}>
                ${it.costUsd.toFixed(2)}
              </div>
              <div style={{ color: 'var(--text-secondary, #8d94a3)' }}>
                {it.filesChanged}f
              </div>
              <div style={{ color: 'var(--text-secondary, #8d94a3)' }}>
                {formatDuration(it.durationMs)}
              </div>
              {/* 2026-05-16 Phase 72 — cache-hit indicator. Cyan when
                  >=50% (cheap iter), amber 1-49% (some reuse), gray
                  zero / unknown so the operator can tell the SDK
                  *did* report cache data even when the hit rate was
                  zero (means session was reused but the prompt
                  prefix changed). */}
              <div style={{ color: cachePctColor(cachePct) }}>
                {cachePct === null
                  ? (convTok != null ? `${formatTokens(convTok)}ctx` : '—')
                  : `${cachePct}%${convTok != null ? ` ${formatTokens(convTok)}ctx` : ''}`}
              </div>
            </button>
          );
        })}
      </div>

      {/* Sparkline summary column */}
      <div
        style={{
          display: 'grid',
          gridTemplateRows: `repeat(${hasAnyCacheData ? 6 : 5}, 1fr)`,
          gap: 1,
          alignItems: 'center',
          fontSize: 10,
          color: 'var(--text-tertiary, #8d94a3)',
          minWidth: 130,
        }}
      >
        <SparkRow label="score" values={scores} format={(v) => `${v}`} stroke="var(--sc-cyan, #5eead4)" />
        <SparkRow label="tok"   values={totalTokens} format={formatTokens} stroke="var(--sc-amber, #ffb347)" />
        <SparkRow label="cost"  values={costs}       format={(v) => `$${v.toFixed(2)}`} stroke="var(--sc-green, #6ee7b7)" />
        <SparkRow label="files" values={files}       format={(v) => `${v}f`} stroke="var(--sc-purple, #c4b5fd)" />
        <SparkRow label="dur"   values={durations}   format={formatDurationMs} stroke="var(--text-tertiary, #8d94a3)" />
        {hasAnyCacheData && (
          <SparkRow label="cache" values={cachePcts} format={(v) => `${Math.round(v)}%`} stroke="var(--sc-blue, #60a5fa)" />
        )}
      </div>
    </div>
  );
}

function SparkRow({
  label, values, format, stroke,
}: { label: string; values: number[]; format?: (v: number) => string; stroke: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
      <span style={{ minWidth: 32, textAlign: 'left', letterSpacing: '0.04em' }}>{label}</span>
      <IterSparkline values={values} format={format} stroke={stroke} />
    </div>
  );
}

function formatTokens(v: number): string {
  if (v >= 1_000_000) return `${(v / 1_000_000).toFixed(1)}M`;
  if (v >= 1_000) return `${(v / 1_000).toFixed(0)}K`;
  return `${v}`;
}

function formatDuration(ms: number | null): string {
  if (ms === null || ms === undefined) return '—';
  return formatDurationMs(ms);
}

function formatDurationMs(ms: number): string {
  if (ms <= 0) return '—';
  const totalSec = Math.round(ms / 1000);
  if (totalSec < 60) return `${totalSec}s`;
  const m = Math.floor(totalSec / 60);
  const s = totalSec % 60;
  return s === 0 ? `${m}m` : `${m}m${s}`;
}

function scoreColor(score: number, verdict: string): string {
  if (verdict === 'pass') return 'var(--sc-green, #6ee7b7)';
  if (verdict === 'reject' || verdict.startsWith('stalled') || verdict === 'sloppy') {
    return 'var(--sc-red, #f87171)';
  }
  if (score >= 90) return 'var(--sc-green, #6ee7b7)';
  if (score >= 70) return 'var(--sc-amber, #ffb347)';
  return 'var(--sc-red, #f87171)';
}

/**
 * 2026-05-16 Phase 72 — compute prompt-cache hit rate as a percentage
 * of TOTAL input tokens (cache reads + fresh input). Returns null when
 * the SDK / backend didn't report cache fields for this iter so the
 * caller can render "no cache data" instead of misleading 0%.
 *
 * The hit-rate formula is intentionally based on TOTAL input (not
 * cache_read / inputTokens) because <c>inputTokens</c> on Anthropic
 * billing already excludes the cached portion. Adding cache_read back
 * gives the operator a "what fraction of the prompt would we have
 * paid for if cache was disabled" figure — the real economic value
 * of session reuse.
 */
function cacheHitPercent(it: IterKpi): number | null {
  const r = it.cacheReadTokens ?? null;
  const w = it.cacheWriteTokens ?? null;
  // If neither cache field was reported, we have no data — return
  // null so the UI can render "—" instead of "0%".
  if (r == null && w == null) return null;
  const cacheRead = r ?? 0;
  const freshInput = Math.max(0, it.inputTokens);
  const total = cacheRead + freshInput;
  if (total === 0) return 0;
  return Math.round((cacheRead / total) * 100);
}

function cachePctColor(pct: number | null): string {
  if (pct === null) return 'var(--text-tertiary, #8d94a3)';
  if (pct >= 50) return 'var(--sc-cyan, #5eead4)';
  if (pct >= 1) return 'var(--sc-amber, #ffb347)';
  return 'var(--text-tertiary, #8d94a3)';
}

/**
 * 2026-05-16 Phase 72 — verbose tooltip for the per-iter chip. Lists
 * the full breakdown: fresh input, cache read, cache write, reasoning,
 * conversation size, cost, duration. Lets the operator hover any iter
 * chip to see exactly why a particular iter was expensive.
 */
function buildChipTitle(it: IterKpi): string {
  const lines: string[] = [];
  lines.push(`iter ${it.iter} · ${it.verdict}`);
  lines.push(`score: ${it.score}`);
  lines.push(`cost: $${it.costUsd.toFixed(3)} · ${it.filesChanged} files · ${formatDuration(it.durationMs)}`);
  lines.push(`input: ${formatTokens(it.inputTokens)} fresh + ${formatTokens(it.outputTokens)} output`);
  if (it.cacheReadTokens != null || it.cacheWriteTokens != null) {
    const r = it.cacheReadTokens ?? 0;
    const w = it.cacheWriteTokens ?? 0;
    const pct = cacheHitPercent(it);
    lines.push(`cache: ${formatTokens(r)} read · ${formatTokens(w)} write${pct !== null ? ` (${pct}% hit)` : ''}`);
  }
  if (it.reasoningTokens != null && it.reasoningTokens > 0) {
    lines.push(`reasoning: ${formatTokens(it.reasoningTokens)}`);
  }
  if (it.conversationTokens != null) {
    lines.push(`conversation context: ${formatTokens(it.conversationTokens)} tokens`);
  }
  return lines.join('\n');
}
