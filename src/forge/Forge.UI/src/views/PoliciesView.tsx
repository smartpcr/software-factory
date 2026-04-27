import { useMemo, useState } from 'react';

// Mocked policy catalogue. Eventually these flow from
// Forge.Api/Domain/DragPolicy.cs + a future PolicyEndpoints.cs;
// for now we render them statically so the view has shape.

type Severity = 'info' | 'warn' | 'block';
type Lane = 'drag' | 'gate' | 'merge';

interface Policy {
  id: string;
  lane: Lane;
  title: string;
  scope: string;       // human eyebrow line
  severity: Severity;
  trigger: string;     // condition body, mono-rendered
  description: string; // what happens when it fires
  enabled: boolean;
  fired24h: number;
  lastFired: string;   // relative
}

const POLICIES: Policy[] = [
  // ── Lane 1 — Drag policy (board moves) ──────────────────────────
  {
    id: 'pol-skip-design',
    lane: 'drag',
    title: 'Skip-phase confirm',
    scope: 'Board · drag move',
    severity: 'warn',
    trigger: "card.from === 'backlog' && card.to === 'build'",
    description:
      'Warn before dragging a card past the Design column. Forces a quick confirm so half-baked stories do not sneak into Build.',
    enabled: true,
    fired24h: 4,
    lastFired: '23m ago',
  },
  {
    id: 'pol-reopen-done',
    lane: 'drag',
    title: 'Re-open Done card',
    scope: 'Board · drag move',
    severity: 'warn',
    trigger: "card.from === 'done' && card.to !== 'done'",
    description:
      'Re-opening a shipped card requires explicit confirm. Logs an audit entry with the dragger\'s identity.',
    enabled: true,
    fired24h: 1,
    lastFired: '4h ago',
  },
  {
    id: 'pol-human-gate-pull',
    lane: 'drag',
    title: 'Pull from human-gate column',
    scope: 'Board · drag move',
    severity: 'block',
    trigger: 'card.humanGate && actor.role !== "human"',
    description:
      'AI agents cannot drag cards out of a human-gated column. The card stays put; a toast surfaces who can release the gate.',
    enabled: true,
    fired24h: 2,
    lastFired: '1h ago',
  },
  {
    id: 'pol-wip-limit',
    lane: 'drag',
    title: 'WIP-limit overflow',
    scope: 'Board · drag move',
    severity: 'info',
    trigger: 'column.cards.length >= column.wipLimit',
    description:
      'Soft toast when a column is already at WIP. Does not block; just nudges the team to unblock first.',
    enabled: false,
    fired24h: 0,
    lastFired: 'never',
  },

  // ── Lane 2 — Human gates (block auto-merge) ─────────────────────
  {
    id: 'pol-security-paths',
    lane: 'gate',
    title: 'Security path human gate',
    scope: 'PR · file paths',
    severity: 'block',
    trigger: 'pr.files.match("/security/**") || pr.files.match("/auth/**")',
    description:
      'Any PR that touches /security/ or /auth/ is held for human approval, regardless of CI green or reviewer verdict.',
    enabled: true,
    fired24h: 2,
    lastFired: '12m ago',
  },
  {
    id: 'pol-schema-migration',
    lane: 'gate',
    title: 'Schema migration gate',
    scope: 'PR · file paths',
    severity: 'block',
    trigger: 'pr.files.match("/db/migrations/**")',
    description:
      'Holds DDL changes for human review. Prevents an agent merging a destructive ALTER without a second pair of eyes.',
    enabled: true,
    fired24h: 1,
    lastFired: '6h ago',
  },
  {
    id: 'pol-large-pr',
    lane: 'gate',
    title: 'Large diff threshold',
    scope: 'PR · diff size',
    severity: 'warn',
    trigger: 'pr.additions + pr.deletions > 800',
    description:
      'PRs over 800 changed lines surface a warning chip and request a second reviewer. Does not block merge on its own.',
    enabled: true,
    fired24h: 0,
    lastFired: '2d ago',
  },
  {
    id: 'pol-touches-pipeline',
    lane: 'gate',
    title: 'CI / pipeline files',
    scope: 'PR · file paths',
    severity: 'warn',
    trigger: 'pr.files.match(".github/**") || pr.files.match("/azure-pipelines.yml")',
    description:
      'Edits to pipeline definitions ping the platform team and require their LGTM before auto-merge.',
    enabled: true,
    fired24h: 0,
    lastFired: '3d ago',
  },

  // ── Lane 3 — Auto-merge & branch protection ─────────────────────
  {
    id: 'pol-required-checks',
    lane: 'merge',
    title: 'Required checks',
    scope: 'Branch · main',
    severity: 'block',
    trigger: '["build","unit-tests","integration-tests"].every(c => c.passing)',
    description:
      'main is locked unless these three checks all pass. Auto-merge waits for the slowest one.',
    enabled: true,
    fired24h: 6,
    lastFired: '8m ago',
  },
  {
    id: 'pol-required-reviewers',
    lane: 'merge',
    title: 'One approving review',
    scope: 'Branch · main',
    severity: 'block',
    trigger: 'pr.approvedBy.length >= 1 && !pr.changesRequested',
    description:
      'At least one approving review on main, with no outstanding "changes requested". Reviewer can be agent or human.',
    enabled: true,
    fired24h: 9,
    lastFired: '4m ago',
  },
  {
    id: 'pol-auto-merge',
    lane: 'merge',
    title: 'Auto-merge on green',
    scope: 'PR · arm-ed by author',
    severity: 'info',
    trigger: 'pr.autoMerge && checks.passing && reviewers.approved',
    description:
      'When the author arms auto-merge, the watcher merges as soon as gates pass. Server-side enforced — UI toggle is just the signal.',
    enabled: true,
    fired24h: 3,
    lastFired: '47m ago',
  },
  {
    id: 'pol-freeze',
    lane: 'merge',
    title: 'Sprint freeze window',
    scope: 'Branch · main',
    severity: 'block',
    trigger: 'now > sprint.endsAt - 24h && pr.label !== "hotfix"',
    description:
      'Last 24h of the sprint, only hotfixes merge. Everything else queues until the new sprint cuts.',
    enabled: false,
    fired24h: 0,
    lastFired: 'never',
  },
];

const LANES: Array<{ key: Lane; num: string; title: string; sub: string }> = [
  {
    key: 'drag',
    num: '01',
    title: 'Drag policy',
    sub: 'Board moves — when to confirm, when to block',
  },
  {
    key: 'gate',
    num: '02',
    title: 'Human gates',
    sub: 'Path / size / label rules that hold a PR for a human',
  },
  {
    key: 'merge',
    num: '03',
    title: 'Auto-merge & branch protection',
    sub: 'What must be green before main accepts a commit',
  },
];

const SEVERITY_LABEL: Record<Severity, string> = {
  info: 'info',
  warn: 'warn',
  block: 'block',
};

export function PoliciesView() {
  const [policies, setPolicies] = useState<Policy[]>(POLICIES);

  const counts = useMemo(() => {
    const total = policies.length;
    const enabled = policies.filter((p) => p.enabled).length;
    const fired = policies.reduce((s, p) => s + p.fired24h, 0);
    const blocking = policies.filter((p) => p.enabled && p.severity === 'block').length;
    return { total, enabled, fired, blocking };
  }, [policies]);

  const byLane = useMemo(() => {
    const map = new Map<Lane, Policy[]>();
    for (const p of policies) {
      const list = map.get(p.lane) ?? [];
      list.push(p);
      map.set(p.lane, list);
    }
    return map;
  }, [policies]);

  function toggle(id: string) {
    setPolicies((prev) => prev.map((p) => (p.id === id ? { ...p, enabled: !p.enabled } : p)));
  }

  return (
    <section className="view active" id="view-policies">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">08</span>&nbsp;Policies</span>
            <span className="sep">/</span>
            <span>{counts.enabled}/{counts.total} enabled · {counts.blocking} hard gates</span>
            <span className="sep">/</span>
            <span><span className="live-dot" />{counts.fired} fires today</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Rules & guardrails
          </h1>
        </div>
        <div className="page-head-right">
          <button className="btn btn-secondary">Export YAML</button>
          <button className="btn btn-primary">
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
              <path d="M6 2v8M2 6h8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
            </svg>
            New rule
          </button>
        </div>
      </div>

      <div className="workstreams-stats" style={{ margin: '0 28px 16px' }}>
        <div className="stat-cell">
          <div className="stat-cell-label">Active rules</div>
          <div className="stat-cell-val">{counts.enabled}<span className="unit">of {counts.total}</span></div>
          <div className="stat-cell-trend">{counts.total - counts.enabled} disabled</div>
        </div>
        <div className="stat-cell">
          <div className="stat-cell-label">Hard gates</div>
          <div className="stat-cell-val">{counts.blocking}</div>
          <div className="stat-cell-trend">block merge until satisfied</div>
        </div>
        <div className="stat-cell">
          <div className="stat-cell-label">Fired (24h)</div>
          <div className="stat-cell-val">{counts.fired}</div>
          <div className="stat-cell-trend"><span className="up">+3</span> vs daily avg</div>
        </div>
        <div className="stat-cell">
          <div className="stat-cell-label">Bypassed (24h)</div>
          <div className="stat-cell-val">0</div>
          <div className="stat-cell-trend">no policy was overridden</div>
        </div>
      </div>

      <div className="policies-wrap">
        {LANES.map((lane) => {
          const list = byLane.get(lane.key) ?? [];
          return (
            <div className="policies-lane" key={lane.key}>
              <div className="policies-lane-head">
                <div className="policies-lane-title">
                  <span className="num">{lane.num}</span>
                  <span>{lane.title}</span>
                </div>
                <span className="policies-lane-count">{list.length}</span>
              </div>
              <div style={{ fontSize: 11, color: 'var(--text-tertiary)', marginBottom: 4 }}>{lane.sub}</div>
              {list.map((p) => (
                <PolicyCard key={p.id} p={p} onToggle={() => toggle(p.id)} />
              ))}
              <button className="policies-lane-add">+ new {lane.title.toLowerCase()} rule</button>
            </div>
          );
        })}
      </div>
    </section>
  );
}

function PolicyCard({ p, onToggle }: { p: Policy; onToggle: () => void }) {
  return (
    <div className="policy-card" data-disabled={!p.enabled || undefined} data-severity={p.severity}>
      <div className="policy-card-head">
        <div style={{ minWidth: 0 }}>
          <div className="policy-card-eyebrow">
            <span>{p.scope}</span>
            <span className="sep">·</span>
            <span className={`severity ${p.severity}`}>{SEVERITY_LABEL[p.severity]}</span>
          </div>
          <div className="policy-card-title">{p.title}</div>
        </div>
        <button
          className="policy-toggle"
          data-on={p.enabled || undefined}
          aria-pressed={p.enabled}
          aria-label={p.enabled ? 'Disable rule' : 'Enable rule'}
          onClick={(e) => { e.stopPropagation(); onToggle(); }}
        />
      </div>

      <div className="policy-card-body">{p.description}</div>

      <div className="policy-card-trigger">
        <span className="key">when</span> <span className="op">{p.trigger}</span>
      </div>

      <div className="policy-card-foot">
        <span className="fired">
          {p.fired24h > 0 && <span className="pulse-dot" />}
          fired <span className="num">{p.fired24h}×</span> · 24h
        </span>
        <span>last {p.lastFired}</span>
      </div>
    </div>
  );
}
