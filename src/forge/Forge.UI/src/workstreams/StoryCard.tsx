// forge_implementation.md Phase 28.9 — story card component.
//
// Renders a spawned story with:
//   - Title + status badge (plan → decompose → implement → review → qa → done)
//   - Phase progress bar (current phase index out of 6)
//   - Expandable list of child workstreams with their statuses
//   - Linked artifacts (PRs, planning docs, QA feature folder)

import { useEffect, useState } from 'react';
import { forgeApi, type StorySpawnArtifacts, type StorySpawnDetail, type StorySpawnPhase } from '@/api/forgeApi';
import { useSpawnStore } from '@/state/useSpawnStore';

interface Props {
  storyId: string;
  /** Force expanded state (collapsed by default). */
  defaultExpanded?: boolean;
}

const PHASES: StorySpawnPhase[] = ['plan', 'decompose', 'implement', 'review', 'qa', 'done'];
const PHASE_LABELS: Record<StorySpawnPhase, string> = {
  plan: 'PLAN',
  decompose: 'DECOMPOSE',
  implement: 'IMPLEMENT',
  review: 'REVIEW',
  qa: 'QA',
  done: 'DONE',
  paused: 'PAUSED',
};

export function StoryCard({ storyId, defaultExpanded = false }: Props) {
  const [detail, setDetail] = useState<StorySpawnDetail | null>(null);
  const [artifacts, setArtifacts] = useState<StorySpawnArtifacts | null>(null);
  const [expanded, setExpanded] = useState(defaultExpanded);
  const [err, setErr] = useState<string | null>(null);

  // Re-fetch when the SignalR realtime tick bumps for this story.
  const tick = useSpawnStore((s) => s.ticks[storyId] ?? 0);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const [d, a] = await Promise.all([
          forgeApi.getStorySpawn(storyId),
          forgeApi.getStoryArtifacts(storyId),
        ]);
        if (!cancelled) {
          setDetail(d);
          setArtifacts(a);
        }
      } catch (e: unknown) {
        if (!cancelled) setErr(e instanceof Error ? e.message : 'Failed to load story');
      }
    }
    void load();
    return () => { cancelled = true; };
  }, [storyId, tick]);

  if (err) {
    return (
      <div className="story-card" style={cardStyle}>
        <div style={{ color: 'var(--danger)', fontSize: 12 }}>Story {storyId} — {err}</div>
      </div>
    );
  }

  if (!detail) {
    return (
      <div className="story-card" style={cardStyle}>
        <div style={{ fontSize: 12, color: 'var(--text-tertiary)' }}>Loading {storyId}…</div>
      </div>
    );
  }

  const { spawn, workstreams } = detail;
  const phaseIdx = PHASES.indexOf(spawn.phase);
  const progressPct = Math.round(((phaseIdx + 1) / PHASES.length) * 100);

  // The lifecycle is fully agent-driven (architect → engineer → reviewer →
  // QA). The UI shows status only — never offers manual "merge / approve /
  // advance" actions. The reviewer crew handles approvals + merges; the
  // orchestrator advances phase when gates pass. See
  // memory/feedback_no_fakes_no_manual.md.
  const planPrMerged = !!artifacts?.plan.pr && (workstreams.length > 0 || !!spawn.decompositionJson);
  const decomposeStarted = !!spawn.decompositionJson;
  const qaPrMerged = !!artifacts?.qa.pr && workstreams.some((w) => w.status === 'complete');

  return (
    <div className="story-card" style={cardStyle}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <button
          type="button"
          onClick={() => setExpanded((e) => !e)}
          style={chevronStyle}
          aria-label={expanded ? 'Collapse' : 'Expand'}
        >
          {expanded ? '▾' : '▸'}
        </button>
        <span style={phaseBadgeStyle(spawn.phase)}>{PHASE_LABELS[spawn.phase]}</span>
        <span style={{ fontSize: 11, color: 'var(--text-tertiary)', fontFamily: 'monospace' }}>
          {spawn.storyId}
        </span>
        <span style={{ fontSize: 13, color: 'var(--text-primary)', flex: 1 }}>{spawn.storyTitle}</span>
        {spawn.baseBranch && spawn.baseBranch !== 'main' && (
          <span
            style={{
              fontSize: 10.5,
              color: 'var(--text-tertiary)',
              fontFamily: 'monospace',
              padding: '2px 6px',
              borderRadius: 'var(--radius-xs)',
              border: '1px solid var(--border-subtle)',
            }}
            title="Phase 45 — every PR this spawn produces (plan, workstream, QA, step-defs) targets this branch."
          >
            base: {spawn.baseBranch}
          </span>
        )}
      </div>

      <div style={progressOuterStyle} aria-label="Phase progress">
        <div style={progressInnerStyle(progressPct)} />
        <span style={progressLabelStyle}>{phaseIdx + 1}/{PHASES.length} phases ({progressPct}%)</span>
      </div>

      {err && <div style={{ color: 'var(--danger)', fontSize: 11.5, marginTop: 6 }}>{err}</div>}

      {expanded && (
        <div style={{ display: 'grid', gap: 10, marginTop: 12 }}>
          <Section title={`Workstreams (${workstreams.length})`}>
            {workstreams.length === 0 && (
              <div style={{ fontSize: 11.5, color: 'var(--text-tertiary)' }}>
                No workstreams yet — submit a decomposition first.
              </div>
            )}
            {workstreams.map((w) => (
              <div key={w.id} style={workstreamRowStyle}>
                <span style={{ fontFamily: 'monospace', fontSize: 11, color: 'var(--text-tertiary)' }}>{w.id}</span>
                <span style={{ flex: 1, fontSize: 12 }}>{w.name}</span>
                <span style={statusPillStyle(w.status)}>{w.status}</span>
                {w.deps.length > 0 && (
                  <span
                    style={{ fontSize: 10, color: 'var(--text-tertiary)' }}
                    title={`Depends on: ${w.deps.join(', ')}`}
                  >
                    ⮨ {w.deps.length}
                  </span>
                )}
              </div>
            ))}
          </Section>

          <Section title="Artifacts">
            {artifacts ? (
              <div style={{ display: 'grid', gap: 6, fontSize: 11.5 }}>
                <ArtifactRow
                  label="Plan PR"
                  value={artifacts.plan.pr
                    ? `${artifacts.plan.pr} ${planPrMerged ? '(merged ✓)' : '(open — needs merge)'}`
                    : '(not yet opened)'}
                  branch={artifacts.plan.branch}
                  files={artifacts.plan.files}
                />
                <ArtifactRow
                  label="Decomposition"
                  value={
                    !decomposeStarted ? '(not yet started)'
                      : spawn.decompositionApproved ? 'approved ✓'
                        : 'submitted (awaiting approval)'
                  }
                  branch={null}
                  files={null}
                />
                <ArtifactRow
                  label="Implementation PRs"
                  value={artifacts.implement.prs.length ? artifacts.implement.prs.join(', ') : '(none)'}
                  branch={null}
                  files={null}
                />
                <ArtifactRow
                  label="QA PR"
                  value={artifacts.qa.pr
                    ? `${artifacts.qa.pr} ${qaPrMerged ? '(merged ✓)' : '(open — needs merge)'}`
                    : '(not yet opened)'}
                  branch={artifacts.qa.branch}
                  files={artifacts.qa.files}
                />
              </div>
            ) : (
              <div style={{ fontSize: 11, color: 'var(--text-tertiary)' }}>Loading…</div>
            )}
          </Section>

        </div>
      )}
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div>
      <div style={{
        fontSize: 10,
        textTransform: 'uppercase',
        letterSpacing: 0.5,
        color: 'var(--text-tertiary)',
        marginBottom: 4,
      }}>
        {title}
      </div>
      <div style={{ display: 'grid', gap: 4 }}>{children}</div>
    </div>
  );
}

function ArtifactRow({
  label,
  value,
  branch,
  files,
}: { label: string; value: string; branch: string | null; files: string[] | null }) {
  return (
    <div style={{ display: 'grid', gridTemplateColumns: '120px 1fr', gap: 8 }}>
      <span style={{ color: 'var(--text-tertiary)' }}>{label}</span>
      <span style={{ color: 'var(--text-primary)' }}>
        {value}
        {branch && (
          <span style={{ color: 'var(--text-tertiary)', marginLeft: 6, fontFamily: 'monospace', fontSize: 10.5 }}>
            ({branch})
          </span>
        )}
        {files && files.length > 0 && (
          <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)', marginTop: 2 }}>
            {files.slice(0, 4).map((f) => <div key={f}>{f}</div>)}
          </div>
        )}
      </span>
    </div>
  );
}

const cardStyle: React.CSSProperties = {
  border: '1px solid var(--border-strong)',
  borderRadius: 'var(--radius-md)',
  background: 'var(--bg-elevated)',
  padding: 12,
  marginBottom: 10,
};

function phaseBadgeStyle(phase: StorySpawnPhase): React.CSSProperties {
  const phaseColors: Record<StorySpawnPhase, [string, string]> = {
    plan: ['#1e3a5f', '#7cc1ff'],
    decompose: ['#3a2d5f', '#b8a0ff'],
    implement: ['#2d5f3a', '#7cffb8'],
    review: ['#5f4d2d', '#ffd57c'],
    qa: ['#5f2d3a', '#ff7caa'],
    done: ['#3d3d3d', '#a0a0a0'],
    paused: ['#5f4d00', '#ffb400'],
  };
  const [bg, fg] = phaseColors[phase];
  return {
    background: bg,
    color: fg,
    fontSize: 10,
    fontWeight: 700,
    letterSpacing: 0.5,
    padding: '3px 8px',
    borderRadius: 999,
  };
}

const progressOuterStyle: React.CSSProperties = {
  position: 'relative',
  height: 6,
  marginTop: 10,
  background: 'var(--bg-surface)',
  borderRadius: 999,
  overflow: 'hidden',
};

function progressInnerStyle(pct: number): React.CSSProperties {
  return {
    width: `${pct}%`,
    height: '100%',
    background: 'linear-gradient(90deg, #4f8cff, #66e5b8)',
    transition: 'width 220ms ease',
  };
}

const progressLabelStyle: React.CSSProperties = {
  position: 'absolute',
  right: 0,
  top: -16,
  fontSize: 10,
  color: 'var(--text-tertiary)',
};

const chevronStyle: React.CSSProperties = {
  background: 'transparent',
  border: 0,
  cursor: 'pointer',
  color: 'var(--text-tertiary)',
  fontSize: 12,
  width: 18,
};

const workstreamRowStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 8,
  padding: '4px 6px',
  borderRadius: 'var(--radius-sm)',
  background: 'var(--bg-surface)',
};

function statusPillStyle(status: string): React.CSSProperties {
  const colors: Record<string, [string, string]> = {
    planning:  ['#2c3e50', '#aac6e5'],
    active:    ['#1e5f3a', '#a0e8c4'],
    blocked:   ['#5f3a1e', '#ffc28c'],
    review:    ['#5f4d2d', '#ffd57c'],
    merging:   ['#4d5f2d', '#d5ff7c'],
    complete:  ['#3d3d3d', '#a0a0a0'],
  };
  const [bg, fg] = colors[status] ?? ['#3d3d3d', '#bbb'];
  return {
    background: bg,
    color: fg,
    fontSize: 10,
    fontWeight: 700,
    padding: '2px 8px',
    borderRadius: 999,
    textTransform: 'uppercase',
    letterSpacing: 0.4,
  };
}
