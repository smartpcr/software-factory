import { useEffect, useMemo, useRef, useState } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { forgeApi } from '@/api/forgeApi';

interface Props {
  storyId: string;
  docId: string;             // 'doc:architecture' | 'doc:tech-spec' | …
  planPr?: string | null;
  planBranch?: string | null;
}

interface Section {
  id: string;       // anchor id (slug)
  no: string;       // numbering — '0', '1', '1.1', '2.3' …
  title: string;
  level: number;    // heading level 1..6 (h1..h6)
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

  const [activeId, setActiveId] = useState<string>(sections[0]?.id ?? '');
  const bodyRef = useRef<HTMLDivElement>(null);
  const sectionRefs = useRef<Record<string, HTMLElement | null>>({});

  useEffect(() => {
    setActiveId(sections[0]?.id ?? '');
    if (bodyRef.current) bodyRef.current.scrollTop = 0;
  }, [docId]); // reset only when the doc selection changes

  const onTocClick = (id: string) => {
    setActiveId(id);
    const el = sectionRefs.current[id];
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
          <ul className="doc-toc-tree">
            {sections.map((s) => (
              <li
                key={s.id}
                className={`doc-toc-item L${s.level}${s.id === activeId ? ' active' : ''}`}
                onClick={() => onTocClick(s.id)}
                title={`H${s.level} · §${s.no} ${s.title}`}
              >
                <span className="bullet" aria-hidden>{s.level === 1 ? '▌' : s.level === 2 ? '◆' : '·'}</span>
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
              key={s.id}
              className={`doc-section doc-section--L${s.level}`}
              ref={(el) => { sectionRefs.current[s.id] = el; }}
            >
              <SectionHeading level={s.level} no={s.no} title={s.title} />
              <SectionBody body={s.bodyMd} />
            </section>
          ))
        )}
      </div>
    </div>
  );
}

function SectionHeading({ level, no, title }: { level: number; no: string; title: string }) {
  // Render as the matching native heading level so screen readers and the
  // browser's outliner pick up the document structure correctly. CSS scales
  // the visual size by .doc-section--L{level}.
  const Tag = (`h${Math.min(level, 6)}` as 'h1' | 'h2' | 'h3' | 'h4' | 'h5' | 'h6');
  return (
    <Tag className={`doc-section-h doc-section-h--L${level}`}>
      <span className="doc-section-no">§ {no}</span>
      <span className="doc-section-t">{level === 1 ? title : title.toUpperCase()}</span>
    </Tag>
  );
}

/** Section body — full GFM markdown (paragraphs, fenced code, lists, tables,
 *  task lists, strikethrough) via react-markdown + remark-gfm. The
 *  surrounding <article> handles section heading + numbering; we render only
 *  the body markdown here. */
function SectionBody({ body }: { body: string }) {
  return (
    <div className="doc-md">
      <ReactMarkdown remarkPlugins={[remarkGfm]}>{body}</ReactMarkdown>
    </div>
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

/**
 * Splits a markdown document into a flat ordered list of sections, one per
 * heading at any level (h1–h6). The TOC then renders the list as a tree by
 * indenting each item to its level.
 *
 * The document's `# Title` is emitted as the first section (level 1) UNLESS
 * extractTitle() already pulled it for the page header — in which case we
 * still want it in the TOC so the reader can scroll back to "the top".
 *
 * Heading number extraction handles common patterns:
 *   `# 1. Foo`, `## 1.2 Bar`, `### 1.2.3 Baz`, `## §1.2 Qux`
 * If the heading doesn't carry a numeric prefix, we synthesize one from
 * the running counter at this level (so a doc with no numbering still
 * shows §1 / §2 in the TOC).
 */
function splitMarkdownIntoSections(md: string): Section[] {
  if (!md) return [];
  const lines = md.split('\n');
  const sections: Section[] = [];

  let inFence = false;
  // Running counters per heading level so a doc with mixed/missing numbering
  // still gets a stable §x.y.z label. Counters reset per parent.
  const counters = [0, 0, 0, 0, 0, 0]; // 1-indexed in code, 0-indexed in array
  // Track the previous heading level so we can reset deeper counters when we
  // unwind back to a higher level.
  let prevLevel = 0;

  let preface: string[] = [];
  let started = false;
  let current: { id: string; no: string; title: string; level: number; lines: string[] } | null = null;

  const slugCounts = new Map<string, number>();
  const slugify = (s: string): string => {
    const base = s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'sec';
    const n = (slugCounts.get(base) ?? 0) + 1;
    slugCounts.set(base, n);
    return n === 1 ? base : `${base}-${n}`;
  };

  for (const line of lines) {
    // Skip headings inside fenced code blocks.
    if (line.startsWith('```')) {
      inFence = !inFence;
      if (current) current.lines.push(line);
      else preface.push(line);
      continue;
    }
    if (inFence) {
      if (current) current.lines.push(line);
      else preface.push(line);
      continue;
    }

    const h = /^(#{1,6})\s+(?:§?\s*)?(?:(\d+(?:\.\d+)*)[.\s]+)?(.+?)\s*#*\s*$/.exec(line);
    if (h) {
      // Close out the previous section.
      if (current) {
        sections.push({
          id: current.id,
          no: current.no,
          title: current.title,
          level: current.level,
          bodyMd: current.lines.join('\n').trim(),
        });
      }
      const level = h[1].length;
      const explicitNo = h[2];
      const title = h[3].trim();

      // Maintain hierarchical counters. If we descended below the previous
      // level, reset deeper counters. If we went up, leave shallower
      // counters as-is so siblings keep numbering.
      if (level > prevLevel) {
        for (let i = prevLevel; i < level - 1; i++) counters[i] = Math.max(counters[i], 1);
      }
      if (level <= prevLevel) {
        // Reset every level deeper than the new one.
        for (let i = level; i < counters.length; i++) counters[i] = 0;
      }
      counters[level - 1] = (counters[level - 1] || 0) + 1;
      prevLevel = level;

      const synthNo = counters.slice(0, level).map((n) => n || 1).join('.');
      const no = explicitNo ?? synthNo;
      current = { id: slugify(`${no}-${title}`), no, title, level, lines: [] };
      started = true;
      continue;
    }
    if (current) {
      current.lines.push(line);
    } else if (!started) {
      preface.push(line);
    }
  }
  if (current) {
    sections.push({
      id: current.id,
      no: current.no,
      title: current.title,
      level: current.level,
      bodyMd: current.lines.join('\n').trim(),
    });
  }

  // Preface (text before any heading) → synthetic "Overview" entry.
  const prefaceText = preface.join('\n').trim();
  if (prefaceText) {
    sections.unshift({ id: 'overview', no: '0', title: 'Overview', level: 1, bodyMd: prefaceText });
  }
  return sections;
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
