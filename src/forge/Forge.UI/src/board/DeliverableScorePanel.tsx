// Phase 14.10 — Score panel on the work-item detail.
//
// Renders the iteration timeline for a work item: one chip per
// `Deliverable` row (1..N), color-coded by verdict. Click any chip to
// expand the dim breakdown + evaluator notes inline. If a judge has
// scored the row (Phase 14.5 dispute), a separate judge chip sits below
// the latest iteration. The "Dispute" affordance shows for product
// owners — server enforces the actual gate.

import { useEffect, useMemo, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useHasRole } from '@/state/useCurrentUserStore';
import type { Deliverable } from '@/types';

interface Props {
  workItemId: string;
}

export function DeliverableScorePanel({ workItemId }: Props) {
  const isPO = useHasRole('product-owner');
  const [rows, setRows] = useState<Deliverable[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<number | null>(null);
  const [disputeFor, setDisputeFor] = useState<Deliverable | null>(null);

  useEffect(() => {
    let cancelled = false;
    setRows(null);
    setError(null);
    setExpanded(null);
    forgeApi.listDeliverablesForWorkItem(workItemId)
      .then((rs) => { if (!cancelled) setRows(rs); })
      .catch((err) => { if (!cancelled) setError(err instanceof Error ? err.message : String(err)); });
    return () => { cancelled = true; };
  }, [workItemId]);

  const sorted = useMemo(() => (rows ?? []).slice().sort((a, b) => a.iter - b.iter), [rows]);
  const latest = sorted[sorted.length - 1];

  if (error) {
    return (
      <div className="detail-section">
        <div className="detail-section-title">Score</div>
        <div style={{ fontSize: 11.5, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>
          Could not load deliverables: {error}
        </div>
      </div>
    );
  }

  if (rows === null) {
    return (
      <div className="detail-section">
        <div className="detail-section-title">Score</div>
        <div style={{ fontSize: 11.5, color: 'var(--text-tertiary)' }}>Loading…</div>
      </div>
    );
  }

  if (rows.length === 0) {
    return (
      <div className="detail-section">
        <div className="detail-section-title">Score</div>
        <div style={{ fontSize: 11.5, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>
          No deliverables scored yet.
        </div>
      </div>
    );
  }

  const finalVerdict = latest.judgeScore !== null
    ? deriveVerdict(latest.judgeScore)
    : latest.verdict;
  const canDispute = isPO && latest && latest.judgeAgentId === null;

  return (
    <div className="detail-section">
      <div className="detail-section-title">
        <span>Score</span>
        <span style={{ fontFamily: 'var(--font-mono)', color: 'var(--text-tertiary)', fontWeight: 400 }}>
          {sorted.length} iter{sorted.length === 1 ? '' : 's'} · final {finalVerdict}
        </span>
      </div>

      {/* Iteration timeline */}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 8 }}>
        {sorted.map((d) => (
          <ScoreChip
            key={d.id}
            label={`#${d.iter}`}
            score={d.score}
            verdict={d.verdict}
            active={expanded === d.id}
            onClick={() => setExpanded(expanded === d.id ? null : d.id)}
          />
        ))}
        {latest.judgeScore !== null && (
          <ScoreChip
            label="judge"
            score={latest.judgeScore}
            verdict={deriveVerdict(latest.judgeScore)}
            active={expanded === -1}
            onClick={() => setExpanded(expanded === -1 ? null : -1)}
            kind="judge"
          />
        )}
        {canDispute && (
          <button
            type="button"
            onClick={() => setDisputeFor(latest)}
            style={disputeButtonStyle}
            title="Submit a judge re-score for the latest iteration"
          >
            ⚖ Dispute
          </button>
        )}
      </div>

      {/* Expanded inline detail for whichever chip is selected */}
      {expanded !== null && (
        expanded === -1 && latest.judgeScore !== null
          ? <JudgeDetail row={latest} />
          : sorted.find((d) => d.id === expanded)
            ? <DeliverableDetail row={sorted.find((d) => d.id === expanded)!} />
            : null
      )}

      {disputeFor && (
        <DisputeModal
          row={disputeFor}
          onClose={() => setDisputeFor(null)}
          onSaved={(updated) => {
            setRows((cur) => (cur ?? []).map((r) => r.id === updated.id ? updated : r));
            setDisputeFor(null);
            setExpanded(-1);
          }}
        />
      )}
    </div>
  );
}

// ── Score chip ───────────────────────────────────────────────────────────

function ScoreChip({ label, score, verdict, active, onClick, kind }: {
  label: string;
  score: number;
  verdict: string;
  active: boolean;
  onClick: () => void;
  kind?: 'judge';
}) {
  const color = verdictColor(verdict);
  return (
    <button
      type="button"
      onClick={onClick}
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 4,
        padding: '3px 9px',
        borderRadius: 999,
        border: `1px solid ${color}`,
        background: active ? `color-mix(in srgb, ${color} 20%, transparent)` : 'transparent',
        color: 'var(--text-primary)',
        fontFamily: 'var(--font-mono, monospace)',
        fontSize: 11,
        cursor: 'pointer',
        ...(kind === 'judge' ? { borderStyle: 'dashed' as const } : {}),
      }}
      title={`${label} — ${verdict} (${score})`}
    >
      <span style={{ color, fontWeight: 600 }}>{label}</span>
      <span>·</span>
      <span style={{ color, fontWeight: 700 }}>{score}</span>
    </button>
  );
}

function verdictColor(verdict: string): string {
  switch (verdict) {
    case 'pass':           return 'rgb(34, 197, 94)';
    case 'iterate':        return 'rgb(180, 112, 0)';
    case 'reject':         return 'rgb(220, 38, 38)';
    case 'stalled':
    case 'stalled-budget': return 'rgb(120, 120, 120)';
    case 'paused-eod':     return 'rgb(20, 110, 245)';
    default:               return 'var(--text-tertiary)';
  }
}

function deriveVerdict(score: number): string {
  if (score >= 90) return 'pass';
  if (score >= 70) return 'iterate';
  return 'reject';
}

// ── Inline detail for an iteration ───────────────────────────────────────

const DIM_LABELS: Array<{ key: string; max: number; name: string }> = [
  { key: 'correctness', max: 40, name: 'Correctness' },
  { key: 'quality',     max: 25, name: 'Quality' },
  { key: 'tests',       max: 15, name: 'Tests' },
  { key: 'scope',       max: 10, name: 'Scope discipline' },
  { key: 'risk',        max: 10, name: 'Risk / safety' },
];

function DeliverableDetail({ row }: { row: Deliverable }) {
  return (
    <div style={detailBoxStyle}>
      <div style={detailRowStyle}>
        <span style={detailLabel}>Iteration</span>
        <span style={detailValue}>#{row.iter}</span>
      </div>
      <div style={detailRowStyle}>
        <span style={detailLabel}>Score</span>
        <span style={{ ...detailValue, color: verdictColor(row.verdict), fontWeight: 700 }}>
          {row.score} · {row.verdict}
        </span>
      </div>
      <div style={detailRowStyle}>
        <span style={detailLabel}>Generator</span>
        <span style={detailValue}>{row.generatorAgentId}</span>
      </div>
      <div style={detailRowStyle}>
        <span style={detailLabel}>Evaluator</span>
        <span style={detailValue}>{row.evaluatorAgentId}</span>
      </div>
      <div style={detailRowStyle}>
        <span style={detailLabel}>Cost</span>
        <span style={detailValue}>
          ${(row.genCostUsd + row.evalCostUsd).toFixed(2)}
          <span style={{ color: 'var(--text-tertiary)', fontSize: 10, marginLeft: 4 }}>
            (gen ${row.genCostUsd.toFixed(2)} + eval ${row.evalCostUsd.toFixed(2)})
          </span>
        </span>
      </div>

      {/* Dim breakdown bars */}
      <div style={{ marginTop: 6 }}>
        {DIM_LABELS.map((d) => {
          const v = row.dims?.[d.key] ?? 0;
          const pct = (v / d.max) * 100;
          return (
            <div key={d.key} style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 10.5, marginBottom: 2 }}>
              <span style={{ width: 110, color: 'var(--text-tertiary)' }}>{d.name}</span>
              <div style={{ flex: 1, height: 6, background: 'var(--bg-sunken)', borderRadius: 3, overflow: 'hidden' }}>
                <div style={{ width: `${pct}%`, height: '100%', background: verdictColor(row.verdict) }} />
              </div>
              <span style={{ fontFamily: 'var(--font-mono, monospace)', width: 50, textAlign: 'right' }}>
                {v} / {d.max}
              </span>
            </div>
          );
        })}
      </div>

      {row.notes && (
        <div style={{ marginTop: 6, fontSize: 11, color: 'var(--text-secondary)', whiteSpace: 'pre-wrap' }}>
          <div style={detailLabel}>Notes</div>
          {row.notes}
        </div>
      )}
    </div>
  );
}

function JudgeDetail({ row }: { row: Deliverable }) {
  if (row.judgeScore === null || row.judgeAgentId === null) return null;
  const verdict = deriveVerdict(row.judgeScore);
  return (
    <div style={{ ...detailBoxStyle, borderStyle: 'dashed' }}>
      <div style={detailRowStyle}>
        <span style={detailLabel}>Judge</span>
        <span style={detailValue}>{row.judgeAgentId}</span>
      </div>
      <div style={detailRowStyle}>
        <span style={detailLabel}>Score</span>
        <span style={{ ...detailValue, color: verdictColor(verdict), fontWeight: 700 }}>
          {row.judgeScore} · {verdict}
          {row.judgeScore >= 90 && <span style={{ color: 'var(--success, rgb(34, 197, 94))', fontWeight: 400, marginLeft: 6 }}>retroactive pass</span>}
        </span>
      </div>
      {row.judgeNotes && (
        <div style={{ marginTop: 6, fontSize: 11, color: 'var(--text-secondary)', whiteSpace: 'pre-wrap' }}>
          <div style={detailLabel}>Notes</div>
          {row.judgeNotes}
        </div>
      )}
    </div>
  );
}

// ── Dispute modal ────────────────────────────────────────────────────────

function DisputeModal({ row, onClose, onSaved }: {
  row: Deliverable;
  onClose: () => void;
  onSaved: (updated: Deliverable) => void;
}) {
  const [judgeAgentId, setJudgeAgentId] = useState('');
  const [judgeScore, setJudgeScore] = useState<number>(90);
  const [judgeNotes, setJudgeNotes] = useState('');
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') onClose(); }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  async function submit() {
    if (!judgeAgentId.trim()) { setErr('Judge agent id is required'); return; }
    if (judgeScore < 0 || judgeScore > 100) { setErr('Score must be 0..100'); return; }
    setSaving(true); setErr(null);
    try {
      const updated = await forgeApi.disputeDeliverable(row.id, {
        judgeAgentId: judgeAgentId.trim(),
        judgeScore,
        judgeNotes: judgeNotes || undefined,
      });
      onSaved(updated);
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
      setSaving(false);
    }
  }

  return (
    <div
      style={{
        position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.45)',
        display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 60,
      }}
      onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
      role="dialog"
      aria-modal="true"
    >
      <div
        style={{
          width: 'min(440px, 100%)',
          background: 'var(--bg-elevated, #1c1c1c)',
          border: '1px solid var(--border)',
          borderRadius: 8,
          padding: 16,
          color: 'var(--text-primary)',
          display: 'grid',
          gap: 10,
        }}
      >
        <div style={{ fontFamily: 'var(--font-mono, monospace)', fontSize: 10, color: 'var(--text-tertiary)', textTransform: 'uppercase', letterSpacing: 0.12 }}>
          dispute · iter #{row.iter} · current {row.verdict} ({row.score})
        </div>
        <h3 style={{ margin: 0, fontSize: 16, fontWeight: 600 }}>Submit judge re-score</h3>
        <div style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>
          One dispute per deliverable. Judge score ≥ 90 flips the work item to <strong>closed</strong> retroactively.
        </div>

        <div>
          <label style={fieldLabelStyle}>Judge agent id</label>
          <input
            type="text" value={judgeAgentId}
            onChange={(e) => setJudgeAgentId(e.target.value)}
            placeholder="e.g. arch-1"
            style={fieldInputStyle}
          />
        </div>
        <div>
          <label style={fieldLabelStyle}>Judge score (0..100)</label>
          <input
            type="number" min={0} max={100} value={judgeScore}
            onChange={(e) => setJudgeScore(Number(e.target.value))}
            style={fieldInputStyle}
          />
        </div>
        <div>
          <label style={fieldLabelStyle}>Judge notes</label>
          <textarea
            value={judgeNotes}
            onChange={(e) => setJudgeNotes(e.target.value)}
            rows={3}
            style={{ ...fieldInputStyle, fontFamily: 'inherit', resize: 'vertical' }}
            placeholder="Why this score?"
          />
        </div>

        {err && <div style={{ fontSize: 11, color: 'var(--danger, rgb(220, 38, 38))' }}>{err}</div>}

        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
          <button onClick={onClose} style={modalSecondaryStyle}>Close</button>
          <button
            onClick={() => void submit()}
            disabled={saving}
            style={{ ...modalPrimaryStyle, opacity: saving ? 0.55 : 1, cursor: saving ? 'not-allowed' : 'pointer' }}
          >
            {saving ? 'Submitting…' : 'Submit dispute'}
          </button>
        </div>
      </div>
    </div>
  );
}

// ── styles ───────────────────────────────────────────────────────────────

const detailBoxStyle: React.CSSProperties = {
  marginTop: 4,
  padding: 10,
  border: '1px solid var(--border)',
  borderRadius: 6,
  background: 'var(--bg-sunken, transparent)',
  display: 'grid',
  gap: 4,
};
const detailRowStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'baseline',
  gap: 8,
  fontSize: 11.5,
};
const detailLabel: React.CSSProperties = {
  fontFamily: 'var(--font-mono, monospace)',
  fontSize: 9.5,
  color: 'var(--text-tertiary)',
  textTransform: 'uppercase',
  letterSpacing: 0.12,
  width: 80,
};
const detailValue: React.CSSProperties = {
  fontFamily: 'var(--font-mono, monospace)',
  fontSize: 11.5,
};

const disputeButtonStyle: React.CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 4,
  padding: '3px 9px',
  borderRadius: 999,
  border: '1px dashed var(--text-tertiary)',
  background: 'transparent',
  color: 'var(--text-secondary)',
  fontFamily: 'var(--font-mono, monospace)',
  fontSize: 11,
  cursor: 'pointer',
};

const fieldLabelStyle: React.CSSProperties = {
  display: 'block',
  fontFamily: 'var(--font-mono, monospace)',
  fontSize: 9.5,
  color: 'var(--text-tertiary)',
  textTransform: 'uppercase',
  letterSpacing: 0.12,
  marginBottom: 3,
};
const fieldInputStyle: React.CSSProperties = {
  width: '100%',
  padding: '5px 8px',
  fontSize: 12,
  borderRadius: 4,
  border: '1px solid var(--border-strong)',
  background: 'var(--bg-base, transparent)',
  color: 'var(--text-primary)',
  boxSizing: 'border-box',
};
const modalSecondaryStyle: React.CSSProperties = {
  padding: '6px 12px',
  borderRadius: 4,
  border: '1px solid var(--border-strong)',
  background: 'transparent',
  color: 'var(--text-primary)',
  cursor: 'pointer',
  fontSize: 12,
};
const modalPrimaryStyle: React.CSSProperties = {
  padding: '6px 12px',
  borderRadius: 4,
  border: '1px solid var(--accent)',
  background: 'var(--accent)',
  color: 'var(--accent-fg, white)',
  cursor: 'pointer',
  fontSize: 12,
  fontWeight: 600,
};
