import { useEffect, useMemo, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';

interface Props {
  storyId: string;
  docId: string;
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
  judgeAgentId: string | null;
  judgeScore: number | null;
  judgeNotes: string | null;
  createdAt: string;
}

/**
 * Per-document conversation tab. Shows the gen↔eval iteration history for one
 * plan doc — mirrors the LiveExecutionView Summary tab but scoped to the
 * synthetic plan-doc workItemId (`plan-doc:{spawn.Id}:{filename}`).
 *
 * One block per iteration:
 *   • header chip:  iter N — score — verdict — gen/evl agents — timestamp
 *   • generator summary section  (markdown, extracted from `## Iteration Summary`)
 *   • evaluator notes section    (markdown, the eval's 3-part review)
 *   • judge override (if a judge re-scored)
 */
export function DocConversationPane({ storyId, docId }: Props) {
  const key = useMemo(() => (docId.startsWith('doc:') ? docId.slice(4) : docId), [docId]);
  const [rows, setRows] = useState<IterRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [totalCost, setTotalCost] = useState(0);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const r = await forgeApi.getStoryArtifactIterations(storyId, key);
        if (cancelled) return;
        setRows(r.iterations);
        setTotalCost(r.totalCostUsd);
        setLoading(false);
      } catch {
        if (!cancelled) setLoading(false);
      }
    }
    setLoading(true);
    void load();
    const id = setInterval(() => { void load(); }, 8_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [storyId, key]);

  if (loading && rows.length === 0) {
    return <div className="tab-placeholder"><em>loading conversation…</em></div>;
  }

  if (rows.length === 0) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ NO ITERATIONS YET</div>
        <div className="sub">
          The architect crew hasn't iterated on this document yet. Once the
          gen↔eval loop starts, each iteration appears here with the generator's
          change summary and the evaluator's score-with-rationale.
        </div>
      </div>
    );
  }

  return (
    <div className="doc-conv">
      <div className="doc-conv-hd">
        <span className="doc-conv-hd-l">▌ {rows.length} {rows.length === 1 ? 'ITERATION' : 'ITERATIONS'}</span>
        <span className="doc-conv-hd-r">
          TOTAL COST · <b>${totalCost.toFixed(2)}</b>
        </span>
      </div>
      <div className="doc-conv-body">
        {rows.map((r) => (
          <article key={r.id} className={`doc-conv-iter ${verdictTone(r.verdict)}`}>
            <header className="doc-conv-iter-hd">
              <span className={`doc-conv-iter-score ${verdictTone(r.verdict)}`}>
                ITER {r.iter} — {r.score}
              </span>
              <span className={`doc-conv-iter-verdict ${verdictTone(r.verdict)}`}>{r.verdict}</span>
              <span className="doc-conv-iter-agents">
                <b>GEN</b> {r.generatorAgentId} <span style={{ opacity: 0.5 }}>↔</span> <b>EVL</b> {r.evaluatorAgentId}
              </span>
              <span className="doc-conv-iter-cost">
                ${(r.genCostUsd + r.evalCostUsd).toFixed(2)}
              </span>
              <span className="doc-conv-iter-when">{new Date(r.createdAt).toLocaleString()}</span>
            </header>

            <section className="doc-conv-iter-section" data-role="generator">
              <div className="doc-conv-iter-section-lbl">GENERATOR · {r.generatorAgentId}</div>
              <div className="doc-conv-iter-section-body">
                {r.generatorSummary && r.generatorSummary.trim()
                  ? <pre className="doc-conv-md">{r.generatorSummary}</pre>
                  : <em style={{ color: 'var(--sc-muted)' }}>(no summary this iteration)</em>}
              </div>
            </section>

            <section className="doc-conv-iter-section" data-role="evaluator">
              <div className="doc-conv-iter-section-lbl">EVALUATOR · {r.evaluatorAgentId}</div>
              <div className="doc-conv-iter-section-body">
                {r.notes && r.notes.trim()
                  ? <pre className="doc-conv-md">{r.notes}</pre>
                  : <em style={{ color: 'var(--sc-muted)' }}>(no evaluator notes)</em>}
              </div>
            </section>

            {r.judgeNotes && (
              <section className="doc-conv-iter-section" data-role="judge">
                <div className="doc-conv-iter-section-lbl">
                  JUDGE · {r.judgeAgentId} · score {r.judgeScore}
                </div>
                <div className="doc-conv-iter-section-body">
                  <pre className="doc-conv-md">{r.judgeNotes}</pre>
                </div>
              </section>
            )}
          </article>
        ))}
      </div>
    </div>
  );
}

function verdictTone(verdict: string): 'pass' | 'iterate' | 'reject' {
  const v = (verdict || '').toLowerCase();
  if (v.startsWith('pass')) return 'pass';
  if (v.startsWith('reject') || v.startsWith('stalled')) return 'reject';
  return 'iterate';
}
