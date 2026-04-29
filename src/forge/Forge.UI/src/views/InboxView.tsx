// Phase 17.7 — Lead inbox view at /inbox.
//
// Three sections:
//   * Pending acks — rows with state='awaiting-ack' for crews this lead leads.
//     Shows a 10s countdown bar; click Acknowledge to flip to awaiting-route.
//   * Strain banners — one per strained crew. Confirm-keep or Reassign-to-crew.
//   * Active queue — open rows grouped by crew, depth + ETA per crew.
import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { forgeApi } from '@/api/forgeApi';
import { useQueueStore, useQueueChannel } from '@/realtime/useQueueStore';
import { useCurrentUserStore } from '@/state/useCurrentUserStore';
import type { Crew, CrewQueueStats, TaskQueueEntry } from '@/types';

export function InboxView(): JSX.Element {
  const me = useCurrentUserStore((s) => s.user);
  const rows  = useQueueStore((s) => s.rows);
  const strain = useQueueStore((s) => s.strain);
  const refresh = useQueueStore((s) => s.refresh);
  const dismissStrain = useQueueStore((s) => s.dismissStrain);
  const navigate = useNavigate();

  const [crews, setCrews] = useState<Crew[]>([]);
  const [statsByCrew, setStatsByCrew] = useState<Record<string, CrewQueueStats>>({});

  // Keep one shared SignalR connection alive for the lifetime of the view.
  useQueueChannel({ userGroup: me?.id ?? null });

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const cs = await forgeApi.listCrews();
        if (!cancelled) setCrews(cs);
      } catch { /* offline */ }
    })();
    void refresh();
    return () => { cancelled = true; };
  }, [refresh]);

  // Phase 17 — every signed-in user sees every open queue row. The Inbox
  // is a single observable surface; access control happens at the action
  // level (ack / route / reassign / cancel are gated server-side by
  // RequireRole.CheckForProduct).
  const myCrews = crews;

  // Refresh stats when myCrews changes.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      const next: Record<string, CrewQueueStats> = {};
      for (const c of myCrews) {
        try { next[c.id] = await forgeApi.getCrewQueueStats(c.id); } catch { /* skip */ }
      }
      if (!cancelled) setStatsByCrew(next);
    })();
    return () => { cancelled = true; };
  }, [myCrews]);

  // Used only to scope the strain banner to crews we know about.
  const myCrewIds = new Set(myCrews.map((c) => c.id));

  const pendingAcks = useMemo(
    () => Object.values(rows).filter((r) => r.state === 'awaiting-ack'),
    [rows],
  );

  const activeRows = useMemo(
    () => Object.values(rows)
      .filter((r) => r.state === 'queued' || r.state === 'picked-up' || r.state === 'awaiting-route')
      .sort((a, b) => a.priorityRank - b.priorityRank || a.enqueuedAt.localeCompare(b.enqueuedAt)),
    [rows],
  );

  const grouped = useMemo(() => {
    const map = new Map<string, TaskQueueEntry[]>();
    for (const r of activeRows) {
      const key = r.routedToCrewId ?? r.targetId;
      const list = map.get(key) ?? [];
      list.push(r);
      map.set(key, list);
    }
    return [...map.entries()];
  }, [activeRows]);

  return (
    <section className="view active" id="view-inbox">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span>📥 Inbox</span>
            <span className="sep">/</span>
            <span>{pendingAcks.length} pending · {activeRows.length} active</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Lead inbox
          </h1>
        </div>
      </div>

      {/* Strain banners (one per strained crew the user leads). */}
      {Object.values(strain).filter((s) => myCrewIds.has(s.crewId)).map((s) => (
        <StrainBanner
          key={s.crewId}
          stats={s}
          otherCrews={myCrews.filter((c) => c.id !== s.crewId)}
          onConfirm={async () => {
            const head = activeRows.find((r) => r.routedToCrewId === s.crewId);
            if (head) await forgeApi.confirmStrain(head.id);
            dismissStrain(s.crewId);
          }}
          onReassign={async (newCrewId) => {
            const head = activeRows.find((r) => r.routedToCrewId === s.crewId);
            if (head) await forgeApi.reassignQueue(head.id, newCrewId);
            dismissStrain(s.crewId);
            void refresh();
          }}
          onOpen={(wid) => navigate(`/work-items/${wid}/live`)}
        />
      ))}

      <div className="inbox-grid">
        <section className="inbox-section">
          <h2 className="inbox-section-title">Pending acks</h2>
          {pendingAcks.length === 0 ? (
            <div className="inbox-empty">Nothing waiting on your acknowledgement.</div>
          ) : (
            <div className="inbox-list">
              {pendingAcks.map((r) => (
                <PendingAckRow key={r.id} row={r} onAck={async () => {
                  await forgeApi.ackQueue(r.id);
                  void refresh();
                }} />
              ))}
            </div>
          )}
        </section>

        <section className="inbox-section">
          <h2 className="inbox-section-title">Active queue</h2>
          {grouped.length === 0 ? (
            <div className="inbox-empty">No active queued or picked-up work.</div>
          ) : (
            grouped.map(([crewId, queueRows]) => {
              const stats = statsByCrew[crewId];
              const crew = myCrews.find((c) => c.id === crewId);
              return (
                <div className="inbox-crew-block" key={crewId}>
                  <div className="inbox-crew-head">
                    <span className="inbox-crew-name">{crew?.name ?? crewId}</span>
                    <span className="inbox-crew-meta">
                      {(() => {
                        const active = queueRows.filter((r) => r.state === 'picked-up').length;
                        const cap = crew?.maxConcurrent ?? 0;
                        const over = cap > 0 && active >= cap;
                        return (
                          <>
                            <strong className={over ? 'inbox-over-cap' : ''}>{active}</strong>
                            {cap > 0 ? <> / {cap} active</> : <> active</>}
                            {over && <span className="inbox-over-cap-pill">⚠ at capacity</span>}
                            <span className="inbox-meta-sep">·</span>
                          </>
                        );
                      })()}
                      depth: <strong>{stats?.depth ?? queueRows.filter((r) => r.state === 'queued').length}</strong>
                      {stats ? <> · ETA: <strong>{stats.etaMinutes}m</strong></> : null}
                      {stats?.strained ? <span className="inbox-strain-pill">strained</span> : null}
                    </span>
                  </div>
                  <div className="inbox-list">
                    {queueRows.map((r) => (
                      <ActiveRow
                        key={r.id}
                        row={r}
                        otherCrews={myCrews.filter((c) => c.id !== crewId)}
                        onOpen={(wid) => navigate(`/work-items/${wid}/live`)}
                        onReassign={async (newCrew) => { await forgeApi.reassignQueue(r.id, newCrew); void refresh(); }}
                        onCancel={async () => { await forgeApi.cancelQueue(r.id); void refresh(); }}
                      />
                    ))}
                  </div>
                </div>
              );
            })
          )}
        </section>
      </div>
    </section>
  );
}

function PendingAckRow({ row, onAck }: { row: TaskQueueEntry; onAck: () => void }): JSX.Element {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), 250);
    return () => window.clearInterval(id);
  }, []);
  const deadline = row.ackRequiredBy ? new Date(row.ackRequiredBy).getTime() : null;
  const enqueued = new Date(row.enqueuedAt).getTime();
  const total = deadline ? Math.max(1, deadline - enqueued) : 1;
  const remaining = deadline ? Math.max(0, deadline - now) : 0;
  const pct = deadline ? Math.max(0, Math.min(100, (remaining / total) * 100)) : 0;

  return (
    <div className="inbox-row" data-priority={row.priorityRank}>
      <div className="inbox-row-main">
        <div className="inbox-row-id">{row.workItemId}</div>
        <div className="inbox-row-meta">
          {row.targetKind === 'team' ? `team · ${row.targetId}` : `crew · ${row.targetId}`}
        </div>
      </div>
      {deadline ? (
        <div className="inbox-countdown">
          <div className="inbox-countdown-bar">
            <div className="inbox-countdown-fill" style={{ width: `${pct}%` }} />
          </div>
          <span className="inbox-countdown-label">
            {remaining > 0 ? `${Math.ceil(remaining / 1000)}s` : 'auto-ack pending'}
          </span>
        </div>
      ) : null}
      <button type="button" className="btn btn-primary inbox-row-cta" onClick={onAck}>
        Acknowledge
      </button>
    </div>
  );
}

function ActiveRow(props: {
  row: TaskQueueEntry;
  otherCrews: Crew[];
  onOpen: (wid: string) => void;
  onReassign: (newCrewId: string) => void;
  onCancel: () => void;
}): JSX.Element {
  const { row, otherCrews, onOpen, onReassign, onCancel } = props;
  return (
    <div className="inbox-row" data-priority={row.priorityRank} data-state={row.state}>
      <div className="inbox-row-main">
        <button type="button" className="inbox-row-id link" onClick={() => onOpen(row.workItemId)}>
          {row.workItemId}
        </button>
        <div className="inbox-row-meta">
          {row.state === 'awaiting-route' ? 'awaiting route' : row.state}
          {row.autoAcked ? <span className="inbox-row-tag">auto-acked</span> : null}
          {row.priorityRank === 0 ? <span className="inbox-row-tag high">high</span> : null}
        </div>
      </div>
      <div className="inbox-row-actions">
        {otherCrews.length > 0 && (
          <select
            className="filter-select"
            defaultValue=""
            onChange={(e) => { if (e.target.value) onReassign(e.target.value); }}
          >
            <option value="" disabled>Reassign…</option>
            {otherCrews.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        )}
        <button type="button" className="btn btn-secondary" onClick={onCancel}>Cancel</button>
      </div>
    </div>
  );
}

function StrainBanner(props: {
  stats: CrewQueueStats;
  otherCrews: Crew[];
  onConfirm: () => void;
  onReassign: (newCrewId: string) => void;
  onOpen: (wid: string) => void;
}): JSX.Element {
  const { stats, otherCrews, onConfirm, onReassign, onOpen } = props;
  return (
    <div className="inbox-strain-banner">
      <div className="inbox-strain-text">
        <strong>{stats.crewId} is strained.</strong>{' '}
        depth <strong>{stats.depth}</strong>{' '}· ETA <strong>{stats.etaMinutes}m</strong>
        {stats.headWorkItemId ? (
          <> · oldest{' '}
            <button type="button" className="link" onClick={() => onOpen(stats.headWorkItemId!)}>
              {stats.headWorkItemId}
            </button>
          </>
        ) : null}
      </div>
      <div className="inbox-strain-actions">
        <button type="button" className="btn btn-secondary" onClick={onConfirm}>
          Confirm — keep in queue
        </button>
        {otherCrews.length > 0 && (
          <select
            className="filter-select"
            defaultValue=""
            onChange={(e) => { if (e.target.value) onReassign(e.target.value); }}
          >
            <option value="" disabled>Reassign to…</option>
            {otherCrews.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        )}
      </div>
    </div>
  );
}

