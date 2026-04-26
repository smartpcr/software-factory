import { useEffect, useState } from 'react';
import type { Crew } from '@/types';
import { forgeApi } from '@/api/forgeApi';
import { useStateStore } from '@/state/useStateStore';
import { sampleState } from '@/sampleData';
import { useHasRole } from '@/state/useCurrentUserStore';

// Phase 10.7 — Crews dashboard (Product Owner only).
// Live salary readouts depend on the Phase 11 cost ledger which isn't built
// yet; this view ships the structure + crew/agent metrics derived from the
// existing seed data so the layout is real today.
export function CrewsView() {
  const isPO = useHasRole('product-owner');
  const apiState = useStateStore((s) => s.state);
  const overviewAgents = apiState?.overviewAgents ?? sampleState.overviewAgents;
  const cards = apiState?.cards ?? sampleState.cards;

  const [crews, setCrews] = useState<Crew[]>([]);
  useEffect(() => {
    forgeApi.listCrews().then(setCrews).catch(() => setCrews([]));
  }, []);

  if (!isPO) {
    return (
      <section className="view active" id="view-crews">
        <div className="page-head">
          <div className="page-head-left">
            <h1 className="page-title"><span className="mark" />Crews</h1>
          </div>
        </div>
        <div className="empty-state">
          <div className="title">Restricted</div>
          <div className="sub">
            The Crews dashboard is visible to Product Owners only. If that's a mistake,
            ask an existing PO to promote your account.
          </div>
        </div>
      </section>
    );
  }

  // Derive activity per crew by mapping agent prefixes to crew ids.
  const agentsByCrew = bucketAgents(overviewAgents);
  const cardsByAgent = bucketCardsByAgent(cards);

  return (
    <section className="view active" id="view-crews">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">10</span>&nbsp;Crews</span>
            <span className="sep">/</span>
            <span>{crews.length} crew{crews.length === 1 ? '' : 's'}</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Crews supervisory view
          </h1>
        </div>
      </div>

      <div className="workstream-grid-wrap">
        <div className="workstream-grid">
          {crews.map((c) => {
            const members = agentsByCrew[crewKey(c.id)] ?? [];
            const inFlight = members.reduce((n, a) => n + (cardsByAgent[a.id]?.filter((card) => card.col === 'build' || card.col === 'review').length ?? 0), 0);
            const blocked = members.reduce((n, a) => n + (cardsByAgent[a.id]?.filter((card) => card.blocked).length ?? 0), 0);
            return (
              <article key={c.id} className="ws-card" style={{ display: 'grid', gap: 10 }}>
                <header className="ws-card-head" style={{ alignItems: 'center', gap: 10 }}>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div className="ws-card-title">{c.name}</div>
                    <div className="ws-card-sub">{c.summary || `${members.length} agent${members.length === 1 ? '' : 's'}`}</div>
                  </div>
                  <span style={modelPillStyle}>{c.model}</span>
                </header>

                <div className="ws-card-stats">
                  <div><span className="num">{members.length}</span><span className="lbl">agents</span></div>
                  <div><span className="num">{inFlight}</span><span className="lbl">in flight</span></div>
                  <div><span className="num">{blocked}</span><span className="lbl">blocked</span></div>
                </div>

                <div className="ws-card-section">
                  <div className="ws-card-section-title">Members</div>
                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                    {members.length === 0 && (
                      <div style={{ fontSize: 11, color: 'var(--text-tertiary)' }}>No agents assigned yet.</div>
                    )}
                    {members.map((a) => (
                      <span key={a.id} style={agentChipStyle(a.id === c.lead)}>
                        {a.id} · {a.name}
                        {a.id === c.lead && <span style={{ marginLeft: 4, opacity: 0.7 }}>· lead</span>}
                      </span>
                    ))}
                  </div>
                </div>

                <div className="ws-card-section">
                  <div className="ws-card-section-title">Pricing</div>
                  <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', display: 'grid', gap: 2, fontFamily: 'var(--font-mono, monospace)' }}>
                    <div>input · ${Number(c.modelInputPricePerMTok).toFixed(2)} / MTok</div>
                    <div>output · ${Number(c.modelOutputPricePerMTok).toFixed(2)} / MTok</div>
                  </div>
                </div>

                <footer className="ws-card-foot" style={{ paddingTop: 8 }}>
                  <span style={{ fontSize: 10.5, color: 'var(--text-tertiary)' }}>
                    Live salary readout lands in Phase 11 (cost ledger).
                  </span>
                </footer>
              </article>
            );
          })}
        </div>

        {crews.length === 0 && (
          <div className="empty-state">
            <div className="title">No crews yet</div>
            <div className="sub">Crews are seeded automatically from the prototype data; if you see this, the seed run hasn't completed yet — refresh in a moment.</div>
          </div>
        )}
      </div>
    </section>
  );
}

// Map agent id like "E4" → "crew-eng" using the prototype's prefix convention.
function crewKey(crewId: string): string {
  switch (crewId) {
    case 'crew-eng':  return 'E';
    case 'crew-rev':  return 'R';
    case 'crew-arch': return 'A';
    case 'crew-qa':   return 'Q';
    default:          return crewId;
  }
}

interface AgentLite { id: string; name: string }

function bucketAgents(agents: AgentLite[]): Record<string, AgentLite[]> {
  const out: Record<string, AgentLite[]> = {};
  for (const a of agents) {
    const key = a.id.charAt(0).toUpperCase();
    (out[key] ??= []).push(a);
  }
  return out;
}

function bucketCardsByAgent(cards: { agent?: { id: string } | null; col?: string; blocked?: boolean }[]): Record<string, typeof cards> {
  const out: Record<string, typeof cards> = {};
  for (const c of cards) {
    const id = c.agent?.id;
    if (!id) continue;
    (out[id] ??= []).push(c);
  }
  return out;
}

const modelPillStyle: React.CSSProperties = {
  fontSize: 10,
  fontWeight: 700,
  letterSpacing: 0.4,
  textTransform: 'uppercase',
  padding: '3px 8px',
  borderRadius: 999,
  border: '1px solid var(--accent)',
  color: 'var(--accent)',
  background: 'var(--accent-soft, transparent)',
  whiteSpace: 'nowrap',
};

function agentChipStyle(isLead: boolean): React.CSSProperties {
  return {
    fontSize: 11,
    padding: '2px 8px',
    borderRadius: 999,
    border: `1px solid ${isLead ? 'var(--accent)' : 'var(--border-strong)'}`,
    color: isLead ? 'var(--accent)' : 'var(--text-secondary)',
    fontFamily: 'var(--font-mono, monospace)',
  };
}
