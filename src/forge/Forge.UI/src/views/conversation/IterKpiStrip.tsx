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
          return (
            <button
              key={it.iter}
              type="button"
              onClick={() => onSelectIter?.(it.iter)}
              title={`iter ${it.iter} · ${it.verdict} · ${(it.costUsd).toFixed(3)} USD · ${it.filesChanged} files · ${formatDuration(it.durationMs)}`}
              style={{
                display: 'grid',
                gridTemplateRows: 'repeat(5, 1fr)',
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
            </button>
          );
        })}
      </div>

      {/* Sparkline summary column */}
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
        <SparkRow label="score" values={scores} format={(v) => `${v}`} stroke="var(--sc-cyan, #5eead4)" />
        <SparkRow label="tok"   values={totalTokens} format={formatTokens} stroke="var(--sc-amber, #ffb347)" />
        <SparkRow label="cost"  values={costs}       format={(v) => `$${v.toFixed(2)}`} stroke="var(--sc-green, #6ee7b7)" />
        <SparkRow label="files" values={files}       format={(v) => `${v}f`} stroke="var(--sc-purple, #c4b5fd)" />
        <SparkRow label="dur"   values={durations}   format={formatDurationMs} stroke="var(--text-tertiary, #8d94a3)" />
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
