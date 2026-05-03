import { useEffect, useMemo, useRef, useState } from 'react';

interface Props {
  docId: string;             // 'doc:architecture' | 'doc:tech-spec' | …
  planPr?: string | null;
  planBranch?: string | null;
}

interface Section {
  no: string;       // '0.0', '1.0', …
  title: string;    // 'Context', 'Topology', …
  body: string;     // markdown-ish; we render a tiny subset (## / paragraphs / code-blocks)
}

interface DocMock {
  title: string;
  subtitle: string;
  badges: string[];                    // tags / chips below the subtitle
  rev: string;                         // 'rev 6.1'
  pp: number;                          // paper-pages count
  lastEditAgo: string;                 // '2h ago'
  contributors: { id: string; role: 'arch-lead' | 'eng-lead' | 'arch-02' | 'arch-04' | 'arch-05' | 'arch-08' | 'sec-01' }[];
  status: 'draft' | 'review' | 'merged';
  sections: Section[];
}

/** Mocked doc bodies — one entry per doc key from SwarmMasterPanel.PLAN_DOCS. */
const MOCKS: Record<string, DocMock> = {
  'architecture': {
    title: 'Streaming Ingestion · Backpressure & Sealing',
    subtitle: 'tenant-aware fan-out · token-bucket throttling · sealer state machine · WAL coupling',
    badges: ['arch-lead', 'eng-lead', 'arch-04'],
    rev: 'rev 6.1',
    pp: 78,
    lastEditAgo: '2h ago',
    contributors: [
      { id: 'arch-lead', role: 'arch-lead' },
      { id: 'eng-lead',  role: 'eng-lead' },
      { id: 'arch-04',   role: 'arch-04' },
    ],
    status: 'merged',
    sections: [
      {
        no: '0.0', title: 'Context',
        body: `The router and sealer compose the hot path between producers and the sealed storage tier. Total budget per event: P99 ≤ 4 ms, end-to-end. The sealer is the synchronization point that guarantees durability before the segment becomes visible to readers.`,
      },
      {
        no: '1.0', title: 'Topology',
        body: `\`\`\`
Producer ─▶ Router ─▶ Fan-out ─▶ [WAL] ─▶ Sealer ─▶ Storage
                          │                              ▲
                          └──────── Backpressure ────────┘
                            (token-bucket per tenant)
\`\`\`

Components: producer SDK · ingress router (per-region) · fan-out worker pool · WAL (per shard) · sealer (one per WAL) · sealed-segment store. Backpressure flows in reverse: sealer → WAL → fan-out → router → producer.`,
      },
      {
        no: '2.0', title: 'Tenant-Aware Routing',
        body: `Router sharding key is \`(tenantId, eventClass)\`. Hot tenants are pinned to a dedicated shard set; cold tenants share the residual pool with consistent hashing. Tenant overrides land via \`/admin/tenants/:id/route\` and are gossiped to every router within 1 RTT.`,
      },
      {
        no: '3.0', title: 'Backpressure Strategy',
        body: `Each tenant gets a token bucket sized to their committed RPS × 1.5 (burst). Tokens refill at the committed rate. When a bucket goes empty the router responds with \`429 Retry-After\`; the producer SDK respects \`Retry-After\` with full jitter. WAL pressure (queue depth > 80%) propagates to the router via a credit channel and shrinks every tenant's bucket proportionally — no single tenant can starve another by going over its commit.`,
      },
      {
        no: '4.0', title: 'Sealer State Machine',
        body: `States: \`open → flushing → sealing → sealed → visible\`. Transitions are write-once: once a segment enters \`sealing\` no further appends are permitted to that segment. Crash recovery replays WAL entries up to the highest sealed offset; entries past that point re-enter \`open\` on a new segment.`,
      },
      {
        no: '5.0', title: 'WAL Coupling',
        body: `WAL is the source of truth for every event between fan-out and seal. The sealer reads WAL in offset order and emits a sealed segment when (a) the segment hits its size cap (64 MiB) or (b) the segment ages past 250 ms — whichever is first. \`fsync\` happens once per sealed segment, not per event.`,
      },
      {
        no: '6.0', title: 'Failure Modes',
        body: `**Sealer crash**: WAL preserved; new sealer instance picks up at last-sealed-offset. RPO = 0, RTO ≤ 5s.
**WAL disk full**: router throttles all tenants on that shard to zero; producers receive \`503\` with \`Retry-After: 30\`.
**Network partition router↔WAL**: router fails open into a per-region overflow buffer (10s ring) until the partition heals.`,
      },
    ],
  },
  'tech-spec': {
    title: 'Tech Spec · Libraries, Patterns, Practices',
    subtitle: 'language choice · concurrency model · serialization · observability',
    badges: ['arch-lead', 'eng-lead'],
    rev: 'rev 3.4',
    pp: 52,
    lastEditAgo: '4h ago',
    contributors: [
      { id: 'arch-lead', role: 'arch-lead' },
      { id: 'eng-lead',  role: 'eng-lead' },
    ],
    status: 'merged',
    sections: [
      { no: '0.0', title: 'Stack',         body: 'Rust 1.78 for the router + sealer hot paths; TypeScript 5.5 for the admin plane. Tonic for gRPC, axum for REST.' },
      { no: '1.0', title: 'Concurrency',   body: 'Single tokio runtime per process; one work-stealing pool sized to physical cores. No nested runtimes. Per-tenant work confined to a single task to keep ordering invariants cheap.' },
      { no: '2.0', title: 'Serialization', body: 'Wire format: Cap\'n Proto. On-disk segment format: Parquet with snappy. JSON only at admin boundary.' },
      { no: '3.0', title: 'Observability', body: 'OpenTelemetry traces span the router→WAL→sealer path. Per-tenant histograms for ingest latency, WAL queue depth, sealer flush time. Logs in structured JSON, single line per event.' },
    ],
  },
  'implementation-plan': {
    title: 'Implementation Plan · Phase → Stage → Step',
    subtitle: 'six phases · twenty-three stages · gated checkpoints',
    badges: ['arch-lead', 'eng-lead'],
    rev: 'rev 2.0',
    pp: 41,
    lastEditAgo: '6h ago',
    contributors: [
      { id: 'arch-lead', role: 'arch-lead' },
      { id: 'eng-lead',  role: 'eng-lead' },
    ],
    status: 'merged',
    sections: [
      { no: '0.0', title: 'Overview',           body: 'Phases land in order. Each phase ends in a deployable cut; no big-bang merge.' },
      { no: '1.0', title: 'P01 — Skeleton',     body: 'Project scaffold, CI green on a hello-world router. Token-bucket primitive in isolation with property tests.' },
      { no: '2.0', title: 'P02 — Single-Tenant',body: 'End-to-end ingest on one tenant, one WAL, one sealer. No backpressure yet — synchronous stop on overflow.' },
      { no: '3.0', title: 'P03 — Multi-Tenant', body: 'Tenant routing table, per-tenant buckets, burst credit. Load test: 10 tenants × 10 KRPS each.' },
      { no: '4.0', title: 'P04 — Backpressure', body: 'Credit channel router←WAL. 429 path. Producer SDK retry-after compliance test.' },
      { no: '5.0', title: 'P05 — Crash Recovery', body: 'Sealer crash drill, WAL replay, RPO = 0 verification. Soak test 24h with chaos injection.' },
      { no: '6.0', title: 'P06 — Hardening',    body: 'Per-tenant SLO dashboards. Admin override audit trail. Production canary in one region for 7 days before global.' },
    ],
  },
  'e2e-scenarios': {
    title: 'E2E Scenarios · Gherkin BDD per Phase',
    subtitle: 'one feature file per phase · acceptance criteria as living tests',
    badges: ['arch-lead', 'arch-02'],
    rev: 'rev 1.5',
    pp: 38,
    lastEditAgo: '8h ago',
    contributors: [
      { id: 'arch-lead', role: 'arch-lead' },
      { id: 'arch-02',   role: 'arch-02' },
    ],
    status: 'merged',
    sections: [
      { no: '0.0', title: 'Conventions', body: 'One feature per phase. Background: cluster-up + clean WAL. Scenarios use \`Given/When/Then\`; outline tables for tenant fan-out variants.' },
      { no: '1.0', title: 'Single-Tenant Feature', body: '```gherkin\nFeature: Single-tenant ingest\n  Scenario: Append within budget\n    Given a tenant "acme" with bucket 1000 rps\n    When the producer sends 800 events/s for 10 s\n    Then P99 ingest latency is under 4 ms\n    And no events are dropped\n```' },
      { no: '2.0', title: 'Multi-Tenant Feature', body: 'Outline scenario: 10 tenants × varying RPS. Asserts no tenant exceeds its bucket and no tenant is starved when neighbors burst.' },
      { no: '3.0', title: 'Backpressure Feature', body: 'WAL queue depth > 80% → router replies 429 with Retry-After. Producer SDK retries with full jitter and observed retry distribution matches expected.' },
      { no: '4.0', title: 'Crash Recovery Feature', body: 'Kill sealer mid-flush. New sealer comes up. Replay WAL. Assert no sealed segment is corrupt and offsets resume contiguously.' },
    ],
  },
  'decomposition': {
    title: 'Decomposition · N Parallel Workstreams',
    subtitle: 'workstreams · dep graph · ownership · timeline',
    badges: ['arch-lead'],
    rev: 'rev 1.0',
    pp: 6,
    lastEditAgo: '1d ago',
    contributors: [
      { id: 'arch-lead', role: 'arch-lead' },
    ],
    status: 'draft',
    sections: [
      { no: '0.0', title: 'Workstreams', body: 'WS-01 graph-schema · WS-02 extraction-pipeline · WS-03 retrieval-engine · WS-04 admin-UI · WS-05 SDK-retry-after.' },
      { no: '1.0', title: 'Dependency Graph', body: '`WS-01 → WS-02 → WS-03`; `WS-04` independent; `WS-05` blocks once WS-03 is past P03.' },
      { no: '2.0', title: 'Ownership',  body: 'Each workstream gets one (generator, evaluator) pair drawn from the day-shift Engineering crew; arch-lead reviews PRs.' },
      { no: '3.0', title: 'Timeline',   body: 'P01–P02 in week 1. P03 in week 2. P04–P05 in weeks 3–4. P06 (hardening + canary) in weeks 5–6.' },
    ],
  },
};

const FALLBACK: DocMock = {
  title: '— no doc selected —',
  subtitle: '',
  badges: [],
  rev: '',
  pp: 0,
  lastEditAgo: '',
  contributors: [],
  status: 'draft',
  sections: [{ no: '0.0', title: 'Empty', body: 'Pick a document on the left to view its body.' }],
};

export function DocumentPane({ docId, planPr, planBranch }: Props) {
  const key = (docId.startsWith('doc:') ? docId.slice(4) : docId) as keyof typeof MOCKS;
  const mock = MOCKS[key] ?? FALLBACK;

  const [activeNo, setActiveNo] = useState<string>(mock.sections[0]?.no ?? '0.0');
  const bodyRef = useRef<HTMLDivElement>(null);
  const sectionRefs = useRef<Record<string, HTMLElement | null>>({});

  // Reset when the selected doc changes.
  useEffect(() => {
    setActiveNo(mock.sections[0]?.no ?? '0.0');
    if (bodyRef.current) bodyRef.current.scrollTop = 0;
  }, [docId, mock.sections]);

  const onTocClick = (no: string) => {
    setActiveNo(no);
    const el = sectionRefs.current[no];
    if (el && bodyRef.current) {
      bodyRef.current.scrollTo({ top: el.offsetTop - 8, behavior: 'smooth' });
    }
  };

  const headerMeta = useMemo(() => {
    const parts: string[] = [];
    if (mock.rev)         parts.push(mock.rev.toUpperCase());
    if (mock.pp > 0)      parts.push(`${mock.pp} pp`);
    if (mock.lastEditAgo) parts.push(`last edit ${mock.lastEditAgo}`);
    return parts.join(' · ');
  }, [mock]);

  return (
    <div className="doc-pane">
      <aside className="doc-toc">
        <div className="doc-toc-hd">▶ TABLE OF CONTENTS</div>
        <ul className="doc-toc-list">
          {mock.sections.map((s) => (
            <li
              key={s.no}
              className={`doc-toc-item${s.no === activeNo ? ' active' : ''}`}
              onClick={() => onTocClick(s.no)}
            >
              <span className="no">§{s.no}</span>
              <span className="t">{s.title}</span>
            </li>
          ))}
        </ul>
      </aside>

      <div className="doc-body" ref={bodyRef}>
        <div className="doc-body-hd">
          <div className="doc-body-hd-l">
            {mock.badges.map((b, i) => (
              <span key={i} className="doc-badge">{b.toUpperCase()}</span>
            ))}
          </div>
          <div className="doc-body-hd-r">{headerMeta}</div>
        </div>

        <h1 className="doc-title">{mock.title}</h1>
        {mock.subtitle && (
          <p className="doc-subtitle">
            {mock.subtitle.split(/\s·\s/).map((piece, i, arr) => (
              <span key={i}>
                {piece}
                {i < arr.length - 1 && <span className="dot"> · </span>}
              </span>
            ))}
          </p>
        )}

        {(planPr || planBranch) && (
          <div className="doc-pr-strip">
            {planPr     && <span>plan PR · <b>{planPr}</b></span>}
            {planBranch && <span>branch · <b>{planBranch}</b></span>}
          </div>
        )}

        {mock.sections.map((s) => (
          <section
            key={s.no}
            className="doc-section"
            ref={(el) => { sectionRefs.current[s.no] = el; }}
          >
            <h2 className="doc-section-h">
              <span className="doc-section-no">§ {s.no}</span>
              <span className="doc-section-t">{s.title.toUpperCase()}</span>
            </h2>
            <SectionBody body={s.body} />
          </section>
        ))}
      </div>
    </div>
  );
}

/** Tiny markdown-ish renderer: paragraphs, fenced code, **bold**. */
function SectionBody({ body }: { body: string }) {
  const blocks = useMemo(() => splitBlocks(body), [body]);
  return (
    <>
      {blocks.map((b, i) =>
        b.kind === 'code'
          ? <pre key={i} className="doc-code">{b.text}</pre>
          : <p key={i} className="doc-p" dangerouslySetInnerHTML={{ __html: renderInline(b.text) }} />,
      )}
    </>
  );
}

function splitBlocks(body: string): { kind: 'p' | 'code'; text: string }[] {
  const out: { kind: 'p' | 'code'; text: string }[] = [];
  const lines = body.split('\n');
  let i = 0;
  while (i < lines.length) {
    if (lines[i].startsWith('```')) {
      const buf: string[] = [];
      i++;
      while (i < lines.length && !lines[i].startsWith('```')) {
        buf.push(lines[i]);
        i++;
      }
      i++; // skip closing fence
      out.push({ kind: 'code', text: buf.join('\n') });
    } else if (lines[i].trim() === '') {
      i++;
    } else {
      const buf: string[] = [];
      while (i < lines.length && lines[i].trim() !== '' && !lines[i].startsWith('```')) {
        buf.push(lines[i]);
        i++;
      }
      out.push({ kind: 'p', text: buf.join('\n') });
    }
  }
  return out;
}

function renderInline(text: string): string {
  // Escape HTML, then re-introduce **bold** + `code`.
  const esc = text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
  return esc
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
}
