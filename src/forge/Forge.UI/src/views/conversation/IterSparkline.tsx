// Insight I.3 (docs/forge/workstream-insights.md) — tiny SVG
// sparkline. No charting library; ~80px × 18px footprint. Used by
// IterKpiStrip's right-edge KPI summary column.

import { useMemo } from 'react';

interface Props {
  /** One value per iteration, ascending. */
  values: number[];
  /** Width in CSS pixels. Default 80. */
  width?: number;
  /** Height in CSS pixels. Default 18. */
  height?: number;
  /** Line stroke. Defaults to the theme accent token. */
  stroke?: string;
  /** Optional formatter for the trailing label. */
  format?: (v: number) => string;
  /** Optional accessible label for screen readers / tooltip. */
  ariaLabel?: string;
}

export function IterSparkline({
  values,
  width = 80,
  height = 18,
  stroke = 'var(--sc-cyan, #5eead4)',
  format,
  ariaLabel,
}: Props): JSX.Element {
  const { points, last } = useMemo(() => sparkPath(values, width, height), [values, width, height]);
  const lastFormatted = values.length > 0
    ? (format ? format(values[values.length - 1]) : values[values.length - 1].toString())
    : '';

  if (values.length === 0) {
    return (
      <span
        role="img"
        aria-label={ariaLabel ?? 'no data yet'}
        style={{ display: 'inline-block', width, height, color: 'var(--text-tertiary, #8d94a3)', fontSize: 10 }}
      >
        —
      </span>
    );
  }

  return (
    <span
      role="img"
      aria-label={ariaLabel ?? `sparkline, ${values.length} samples, latest ${lastFormatted}`}
      title={ariaLabel ?? `latest: ${lastFormatted}`}
      style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}
    >
      <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} aria-hidden="true">
        <polyline
          points={points}
          fill="none"
          stroke={stroke}
          strokeWidth={1}
          strokeLinejoin="round"
          strokeLinecap="round"
          vectorEffect="non-scaling-stroke"
        />
        {last && (
          <circle cx={last.x} cy={last.y} r={1.5} fill={stroke} />
        )}
      </svg>
      <span style={{ fontSize: 10, color: 'var(--text-secondary, #8d94a3)', fontFamily: 'var(--sc-font-mono, monospace)', minWidth: 30 }}>
        {lastFormatted}
      </span>
    </span>
  );
}

function sparkPath(values: number[], width: number, height: number): {
  points: string;
  last: { x: number; y: number } | null;
} {
  if (values.length === 0) return { points: '', last: null };
  if (values.length === 1) {
    const x = width / 2;
    const y = height / 2;
    return { points: `${x},${y}`, last: { x, y } };
  }

  const min = Math.min(...values);
  const max = Math.max(...values);
  const range = max - min || 1;
  const pad = 1.5;
  const innerW = width - pad * 2;
  const innerH = height - pad * 2;
  const step = innerW / (values.length - 1);

  const pts: string[] = [];
  let lastX = 0, lastY = 0;
  for (let i = 0; i < values.length; i++) {
    const x = pad + i * step;
    // Flip Y so larger values render higher on screen.
    const y = pad + innerH - ((values[i] - min) / range) * innerH;
    pts.push(`${x.toFixed(1)},${y.toFixed(1)}`);
    if (i === values.length - 1) { lastX = x; lastY = y; }
  }
  return { points: pts.join(' '), last: { x: lastX, y: lastY } };
}
