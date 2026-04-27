// Phase 14 — bulk-dispatch modal: assign one or more work items to the
// matching AI crew (default Architecture for `track=design` / Plan kind,
// Engineering for `track=dev` / Coding kind), the lead becomes the
// generator, and the runner kickoff fires per-item. Same pipeline shape
// as the New Card flow on Plan & Design.

import { useEffect, useMemo, useRef, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import { avatarFor, agentTooltip } from '@/agentAvatar';
import type { Crew } from '@/types';
import type { WorkItem } from '@/workitems/workItemTypes';

interface Props {
  open: boolean;
  onClose: () => void;
  items: WorkItem[];
}

export function DispatchToAiModal({ open, onClose, items }: Props) {
  const [crews, setCrews] = useState<Crew[]>([]);
  const [crewId, setCrewId] = useState<string>('');
  const [busy, setBusy] = useState(false);
  const showToast = useToastStore((s) => s.show);

  // Derive the dominant track of the selection — design / dev / mixed.
  const dominantTrack = useMemo(() => {
    if (items.length === 0) return null;
    const tracks = new Set(items.map((i) => i.track));
    if (tracks.size === 1) return [...tracks][0];
    return 'mixed' as const;
  }, [items]);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    forgeApi.listCrews()
      .then((cs) => { if (!cancelled) setCrews(cs); })
      .catch(() => { /* ignore */ });
    return () => { cancelled = true; };
  }, [open]);

  // Auto-pick the default crew per the dominant track. For 'mixed', default
  // to engineering (most common) but the operator must confirm.
  useEffect(() => {
    if (!open || crews.length === 0) return;
    const want = dominantTrack === 'design'
      ? ['crew-arch', 'architecture', 'arch']
      : ['crew-eng', 'engineering', 'eng'];
    let found: Crew | undefined;
    for (const w of want) {
      found = crews.find((c) => c.id.toLowerCase() === w
        || c.id.toLowerCase().includes(w)
        || c.name.toLowerCase().includes(w));
      if (found) break;
    }
    if (found) setCrewId(found.id);
  }, [open, crews, dominantTrack]);

  if (!open) return null;

  async function dispatch() {
    if (!crewId) { showToast('Pick a crew first.', 'danger'); return; }
    setBusy(true);
    let assigned = 0;
    let started = 0;
    let queued = 0;
    let failed = 0;
    const crew = crews.find((c) => c.id === crewId);
    const lead = crew?.lead ?? '';

    for (const item of items) {
      // 1. Assign to the crew lead. Server falls back to lead when memberAgentId is omitted.
      try {
        if (lead) {
          await forgeApi.assignCrewTask(crewId, { workItemId: item.id, memberAgentId: lead });
          assigned++;
        }
      } catch {
        failed++;
        continue;
      }

      // 2. Kick off the right runner per-item track. design → /plan, dev → /code.
      const path = item.track === 'design' ? 'plan' : 'code';
      const body = item.track === 'design'
        ? { workItemId: item.id }
        : { stepWorkItemId: item.id };
      try {
        const r = await fetch(`/api/crews/${encodeURIComponent(crewId)}/${path}`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-Forge-User': 'forge-ui' },
          body: JSON.stringify(body),
        });
        if (r.ok) started++;
        else if (r.status === 501) queued++;
        else failed++;
      } catch { failed++; }
    }

    setBusy(false);
    const crewLabel = crew?.name ?? crewId;
    if (failed === 0 && started > 0) {
      showToast(`${started} dispatched to ${crewLabel} · agents running`, 'success');
    } else if (failed === 0) {
      showToast(
        `${assigned} queued for ${crewLabel} lead (runner pending Phase 13.4/13.5)`,
        'info',
      );
    } else {
      showToast(
        `Dispatch partial: ${started + queued} ok, ${failed} failed`,
        'danger',
      );
    }
    onClose();
  }

  return (
    <>
      <div className="modal-backdrop show" onClick={() => { if (!busy) onClose(); }} />
      <div className="modal show" role="dialog" aria-modal="true" style={{ width: 'min(520px, 95vw)' }}>
        <h3 className="modal-title">Dispatch to AI crew</h3>

        <div style={{ display: 'grid', gap: 12, marginBottom: 18 }}>
          <div style={{ fontSize: 12, color: 'var(--text-secondary)', lineHeight: 1.5 }}>
            {items.length === 1
              ? <>Assign <strong>{items[0].id}</strong> to a crew. The crew's lead becomes the generator and the runner kicks off immediately.</>
              : <>Assign <strong>{items.length} work items</strong> to a crew. Each item kicks off the runner appropriate to its track ({dominantTrack === 'design' ? 'plan' : dominantTrack === 'dev' ? 'code' : 'plan/code mixed'}).</>}
          </div>

          {/* Item summary */}
          <div style={{
            maxHeight: 180, overflow: 'auto',
            border: '1px solid var(--border)', borderRadius: 6,
            padding: 6, background: 'var(--bg-sunken, transparent)',
          }}>
            {items.map((it) => (
              <div key={it.id} style={{ display: 'flex', alignItems: 'baseline', gap: 8, fontSize: 11.5, padding: '3px 6px' }}>
                <span style={{ fontFamily: 'var(--font-mono, monospace)', color: 'var(--text-tertiary)', minWidth: 80 }}>{it.id}</span>
                <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{it.title}</span>
                <span style={trackPillStyle(it.track)}>{it.track}/{it.type}</span>
              </div>
            ))}
          </div>

          <div>
            <div style={{ fontSize: 11, color: 'var(--text-secondary)', marginBottom: 4 }}>
              Crew (default {dominantTrack === 'design' ? 'architecture' : dominantTrack === 'dev' ? 'engineering' : 'engineering · selection mixed tracks'})
            </div>
            <div style={{
              padding: '4px 8px', borderRadius: 'var(--radius-sm)',
              border: '1px solid var(--border-strong)', background: 'var(--bg-surface)',
            }}>
              <CrewPicker crews={crews} value={crewId} onChange={setCrewId} />
            </div>
          </div>

          <div style={{ fontSize: 11, color: 'var(--text-tertiary)', lineHeight: 1.5 }}>
            On submit: each item is assigned to the crew lead, then a runner kickoff fires at <code style={{ fontFamily: 'var(--font-mono, monospace)' }}>/api/crews/{'{crew}'}/plan</code> (design) or <code style={{ fontFamily: 'var(--font-mono, monospace)' }}>/code</code> (dev). The runner is currently a Phase 13.4/13.5 stub (501) — assignment lands today, agent execution lands when those wire up.
          </div>
        </div>

        <div className="modal-actions">
          <button className="btn btn-secondary" onClick={onClose} disabled={busy}>Cancel</button>
          <button className="btn btn-primary" onClick={() => void dispatch()} disabled={busy || !crewId || items.length === 0}>
            {busy ? 'Dispatching…' : `Dispatch ${items.length}`}
          </button>
        </div>
      </div>
    </>
  );
}

function trackPillStyle(track: string): React.CSSProperties {
  const accent = track === 'design' ? 'rgb(168, 85, 247)' : 'rgb(20, 110, 245)';
  return {
    fontSize: 9.5,
    padding: '1px 6px',
    borderRadius: 999,
    border: `1px solid ${accent}`,
    color: accent,
    fontFamily: 'var(--font-mono, monospace)',
    textTransform: 'lowercase',
    whiteSpace: 'nowrap',
  };
}

// ── Lead avatar + crew picker (shared shape with CardModal) ─────────────

function LeadAvatar({ id, size = 22 }: { id: string | null | undefined; size?: number }) {
  if (!id) {
    return (
      <span
        title="(no lead)"
        style={{ width: size, height: size, borderRadius: 999, background: 'var(--bg-sunken, #2a2a2a)', display: 'inline-block' }}
      />
    );
  }
  const url = avatarFor(id, null);
  const tip = agentTooltip(id, null, null);
  if (url) {
    return <img src={url} alt={id} title={tip} style={{ width: size, height: size, borderRadius: 999, objectFit: 'cover' }} />;
  }
  return (
    <span
      title={tip}
      style={{
        width: size, height: size, borderRadius: 999, display: 'inline-flex',
        alignItems: 'center', justifyContent: 'center',
        background: 'var(--bg-sunken, #2a2a2a)', color: 'var(--text-secondary)',
        fontFamily: 'var(--font-mono, monospace)', fontSize: size * 0.42,
      }}
    >
      {id.slice(0, 2)}
    </span>
  );
}

function CrewPicker({ crews, value, onChange }: { crews: Crew[]; value: string; onChange: (id: string) => void }) {
  const [open, setOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const popoverRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    function onDown(e: MouseEvent) {
      const t = e.target as Node;
      if (triggerRef.current?.contains(t)) return;
      if (popoverRef.current?.contains(t)) return;
      setOpen(false);
    }
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') setOpen(false); }
    window.addEventListener('mousedown', onDown);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('mousedown', onDown);
      window.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const selected = crews.find((c) => c.id === value) ?? null;

  return (
    <div style={{ position: 'relative' }}>
      <button
        ref={triggerRef}
        type="button"
        onClick={() => setOpen((v) => !v)}
        style={{
          width: '100%', display: 'flex', alignItems: 'center', gap: 8,
          padding: '4px 4px', border: 'none', background: 'transparent',
          color: 'inherit', cursor: 'pointer', textAlign: 'left',
        }}
      >
        {selected ? (
          <>
            <LeadAvatar id={selected.lead} size={22} />
            <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontSize: 12 }}>
              <span style={{ fontWeight: 600 }}>{selected.name}</span>
              {selected.lead && (
                <span style={{ color: 'var(--text-tertiary)', marginLeft: 6, fontFamily: 'var(--font-mono, monospace)', fontSize: 10.5 }}>
                  lead {selected.lead}
                </span>
              )}
            </span>
          </>
        ) : (
          <span style={{ flex: 1, color: 'var(--text-tertiary)', fontStyle: 'italic', fontSize: 12 }}>— pick a crew —</span>
        )}
        <span aria-hidden style={{ color: 'var(--text-tertiary)', fontSize: 10 }}>▾</span>
      </button>
      {open && (
        <div
          ref={popoverRef}
          role="listbox"
          style={{
            position: 'absolute', top: 'calc(100% + 4px)', left: 0, right: 0, zIndex: 30,
            maxHeight: 280, overflow: 'auto',
            background: 'var(--bg-elevated, #1c1c1c)',
            border: '1px solid var(--border)', borderRadius: 6,
            boxShadow: '0 6px 20px rgba(0,0,0,0.35)', padding: 4,
          }}
        >
          {crews.length === 0 ? (
            <div style={{ padding: 12, fontSize: 11, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>Loading crews…</div>
          ) : crews.map((c) => (
            <button
              key={c.id}
              type="button"
              onClick={() => { onChange(c.id); setOpen(false); }}
              style={{
                display: 'flex', alignItems: 'center', gap: 8,
                width: '100%', padding: '6px 8px',
                background: c.id === value ? 'color-mix(in srgb, var(--accent) 14%, transparent)' : 'transparent',
                border: 'none', color: 'inherit', cursor: 'pointer', textAlign: 'left',
                borderRadius: 4,
              }}
            >
              <LeadAvatar id={c.lead} size={26} />
              <span style={{ flex: 1, minWidth: 0 }}>
                <div style={{ fontWeight: 600, fontSize: 12.5 }}>{c.name}</div>
                <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)' }}>
                  {c.lead ? `lead ${c.lead}` : 'no lead'}
                  {c.summary ? ` · ${c.summary}` : ''}
                </div>
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
