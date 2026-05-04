import { useEffect, useMemo, useRef, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';

interface Props {
  storyId: string;
  docId: string;             // 'doc:architecture' | 'doc:tech-spec' | …
  planPr?: string | null;
  planBranch?: string | null;
}

interface Section {
  no: string;       // '0', '1.0', '2.3' …
  title: string;
  bodyMd: string;
}

interface DocFetched {
  content: string;
  exists: boolean;
  source: string | null;
  lastModifiedAt: string | null;
}

interface IterRow {
  id: number;
  iter: number;
  score: number;
  verdict: string;
  generatorAgentId: string;
  evaluatorAgentId: string;
  generatorSummary: string;
  notes: string;
  genCostUsd: number;
  evalCostUsd: number;
  judgeNotes: string | null;
  createdAt: string;
}

interface ArtifactState {
  loading: boolean;
  doc: DocFetched | null;
  iterations: IterRow[];
  totalCostUsd: number;
  error: string | null;
}

export function DocumentPane({ storyId, docId, planPr, planBranch }: Props) {
  const key = useMemo(() => (docId.startsWith('doc:') ? docId.slice(4) : docId), [docId]);
  const [state, setState] = useState<ArtifactState>({
    loading: true, doc: null, iterations: [], totalCostUsd: 0, error: null,
  });

  // Fetch + refresh on a slow cadence so the UI lights up as the LLM produces
  // each iteration. 8s is a balance between live-feel and not hammering EF.
  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const [doc, iters] = await Promise.all([
          forgeApi.getStoryArtifactContent(storyId, key),
          forgeApi.getStoryArtifactIterations(storyId, key),
        ]);
        if (cancelled) return;
        setState({
          loading: false,
          doc: { content: doc.content, exists: doc.exists, source: doc.source ?? null, lastModifiedAt: doc.lastModifiedAt },
          iterations: iters.iterations,
          totalCostUsd: iters.totalCostUsd,
          error: null,
        });
      } catch (e) {
        if (cancelled) return;
        setState((s) => ({ ...s, loading: false, error: (e as Error).message }));
      }
    }
    setState((s) => ({ ...s, loading: true, error: null }));
    void load();
    const id = setInterval(() => { void load(); }, 8_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [storyId, key]);

  const sections = useMemo(() => splitMarkdownIntoSections(state.doc?.content ?? ''), [state.doc?.content]);
  const docTitle = useMemo(() => extractTitle(state.doc?.content ?? '') ?? key, [state.doc?.content, key]);
  const latestIter = state.iterations[state.iterations.length - 1] ?? null;

  const [activeNo, setActiveNo] = useState<string>(sections[0]?.no ?? '0');
  const bodyRef = useRef<HTMLDivElement>(null);
  const sectionRefs = useRef<Record<string, HTMLElement | null>>({});

  useEffect(() => {
    setActiveNo(sections[0]?.no ?? '0');
    if (bodyRef.current) bodyRef.current.scrollTop = 0;
  }, [docId]); // reset only when the doc selection changes

  const onTocClick = (no: string) => {
    setActiveNo(no);
    const el = sectionRefs.current[no];
    if (el && bodyRef.current) {
      bodyRef.current.scrollTo({ top: el.offsetTop - 8, behavior: 'smooth' });
    }
  };

  const headerMeta = useMemo(() => {
    const parts: string[] = [];
    if (latestIter) parts.push(`ITER ${latestIter.iter}`);
    if (latestIter) parts.push(`SCORE ${latestIter.score}`);
    if (state.totalCostUsd > 0) parts.push(`COST $${state.totalCostUsd.toFixed(2)}`);
    if (state.doc?.source) parts.push(state.doc.source.toUpperCase());
    if (state.doc?.lastModifiedAt) {
      parts.push(`UPDATED ${formatAgo(state.doc.lastModifiedAt)}`);
    }
    return parts.join(' · ');
  }, [latestIter, state.totalCostUsd, state.doc]);

  if (state.loading && !state.doc) {
    return <div className="doc-pane"><div className="doc-body"><em style={{ color: 'var(--sc-text-dim)' }}>loading {key}.md …</em></div></div>;
  }

  if (state.error) {
    return <div className="doc-pane"><div className="doc-body"><pre style={{ color: 'var(--sc-red)' }}>{state.error}</pre></div></div>;
  }

  const doc = state.doc;
  const empty = !doc?.exists || sections.length === 0;

  return (
    <div className="doc-pane">
      <aside className="doc-toc">
        <div className="doc-toc-hd">▶ TABLE OF CONTENTS</div>
        {empty ? (
          <div style={{ padding: '0 14px', fontSize: 11, color: 'var(--sc-text-dim)' }}>
            <em>no sections yet</em>
          </div>
        ) : (
          <ul className="doc-toc-list">
            {sections.map((s) => (
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
        )}
      </aside>

      <div className="doc-body" ref={bodyRef}>
        <div className="doc-body-hd">
          <div className="doc-body-hd-l">
            {latestIter && (
              <span className={`doc-badge doc-badge--${verdictTone(latestIter.verdict)}`}>
                {latestIter.verdict.toUpperCase()}
              </span>
            )}
            {latestIter && (
              <>
                <span className="doc-badge">GEN {latestIter.generatorAgentId}</span>
                <span className="doc-badge">EVL {latestIter.evaluatorAgentId}</span>
              </>
            )}
          </div>
          <div className="doc-body-hd-r">{headerMeta || '—'}</div>
        </div>

        <h1 className="doc-title">{docTitle}</h1>

        {(planPr || planBranch) && (
          <div className="doc-pr-strip">
            {planPr     && <span>plan PR · <b>{planPr}</b></span>}
            {planBranch && <span>branch · <b>{planBranch}</b></span>}
          </div>
        )}

        {empty ? (
          <div className="tab-placeholder" style={{ marginTop: 24 }}>
            <div className="lbl">▌ AWAITING ARCHITECT</div>
            <div className="sub">
              The architect crew has not produced this file yet. The orchestrator's
              gen↔eval loop will write it to the plan branch once the iteration
              completes; this view will update automatically.
            </div>
          </div>
        ) : (
          sections.map((s) => (
            <section
              key={s.no}
              className="doc-section"
              ref={(el) => { sectionRefs.current[s.no] = el; }}
            >
              <h2 className="doc-section-h">
                <span className="doc-section-no">§ {s.no}</span>
                <span className="doc-section-t">{s.title.toUpperCase()}</span>
              </h2>
              <SectionBody body={s.bodyMd} />
            </section>
          ))
        )}
      </div>
    </div>
  );
}

/** Tiny markdown-ish renderer: paragraphs, fenced code, **bold**, `inline`,
 *  ordered/unordered lists. Good enough for the architect's plan output. */
function SectionBody({ body }: { body: string }) {
  const blocks = useMemo(() => splitBlocks(body), [body]);
  return (
    <>
      {blocks.map((b, i) => {
        if (b.kind === 'code') return <pre key={i} className="doc-code">{b.text}</pre>;
        if (b.kind === 'list') {
          return (
            <ul key={i} className="doc-list">
              {b.items.map((it, j) => (
                <li key={j} dangerouslySetInnerHTML={{ __html: renderInline(it) }} />
              ))}
            </ul>
          );
        }
        return <p key={i} className="doc-p" dangerouslySetInnerHTML={{ __html: renderInline(b.text) }} />;
      })}
    </>
  );
}

/* ─── markdown helpers ────────────────────────────────────────────────── */

function extractTitle(md: string): string | null {
  const lines = md.split('\n');
  for (const line of lines) {
    if (line.startsWith('# ')) return line.slice(2).trim();
  }
  return null;
}

function splitMarkdownIntoSections(md: string): Section[] {
  if (!md) return [];
  const lines = md.split('\n');
  const sections: Section[] = [];
  let current: { no: string; title: string; lines: string[] } | null = null;

  // Skip leading `# Title` and any preface.
  let started = false;
  let preface: string[] = [];

  for (const line of lines) {
    // Start a new section on `## ...` headings.
    const h2 = /^##\s+(?:(\d+(?:\.\d+)*)\s+)?(.+)$/.exec(line);
    if (h2) {
      if (current) sections.push({ no: current.no, title: current.title, bodyMd: current.lines.join('\n') });
      const num = h2[1] ?? String(sections.length + 1);
      current = { no: num, title: h2[2].trim(), lines: [] };
      started = true;
      continue;
    }
    if (current) {
      current.lines.push(line);
    } else if (!started) {
      // Skip the document title line, capture any preface paragraphs.
      if (line.startsWith('# ')) continue;
      preface.push(line);
    }
  }
  if (current) sections.push({ no: current.no, title: current.title, bodyMd: current.lines.join('\n') });

  // If the doc has preface text but no `## sections`, surface it as a single
  // synthetic "Overview" section so the renderer has something to show.
  if (sections.length === 0 && preface.some((l) => l.trim() !== '')) {
    return [{ no: '0', title: 'Overview', bodyMd: preface.join('\n').trim() }];
  }
  // Or, if there's preface AND sections, prepend it.
  if (preface.some((l) => l.trim() !== '')) {
    sections.unshift({ no: '0', title: 'Overview', bodyMd: preface.join('\n').trim() });
  }
  return sections;
}

type Block =
  | { kind: 'p'; text: string }
  | { kind: 'code'; text: string }
  | { kind: 'list'; items: string[] };

function splitBlocks(body: string): Block[] {
  const out: Block[] = [];
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
      i++; // closing fence
      out.push({ kind: 'code', text: buf.join('\n') });
      continue;
    }
    if (/^\s*[-*+]\s+/.test(lines[i])) {
      const items: string[] = [];
      while (i < lines.length && /^\s*[-*+]\s+/.test(lines[i])) {
        items.push(lines[i].replace(/^\s*[-*+]\s+/, ''));
        i++;
      }
      out.push({ kind: 'list', items });
      continue;
    }
    if (lines[i].trim() === '') {
      i++;
      continue;
    }
    const buf: string[] = [];
    while (i < lines.length && lines[i].trim() !== '' && !lines[i].startsWith('```') && !/^\s*[-*+]\s+/.test(lines[i])) {
      buf.push(lines[i]);
      i++;
    }
    out.push({ kind: 'p', text: buf.join('\n') });
  }
  return out;
}

function renderInline(text: string): string {
  const esc = text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
  return esc
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\*([^*]+)\*/g, '<em>$1</em>');
}

/* ─── verdict / time helpers ──────────────────────────────────────────── */

function verdictTone(verdict: string): 'pass' | 'iterate' | 'reject' {
  const v = verdict.toLowerCase();
  if (v.startsWith('pass')) return 'pass';
  if (v.startsWith('reject') || v.startsWith('stalled')) return 'reject';
  return 'iterate';
}

function formatAgo(iso: string): string {
  const t = Date.parse(iso);
  if (isNaN(t)) return '';
  const ms = Date.now() - t;
  if (ms < 60_000) return `${Math.floor(ms / 1000)}S`;
  if (ms < 3600_000) return `${Math.floor(ms / 60_000)}M`;
  if (ms < 86400_000) return `${Math.floor(ms / 3600_000)}H`;
  return `${Math.floor(ms / 86400_000)}D`;
}
