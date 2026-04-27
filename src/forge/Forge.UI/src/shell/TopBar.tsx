import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useThemeStore } from '@/state/useThemeStore';
import { useProductsStore } from '@/state/useProductsStore';
import { useCurrentUser, useCurrentUserStore, useHasRole } from '@/state/useCurrentUserStore';
import { ProductIcon } from '@/products/ProductIcon';
import { UserMenu } from '@/shell/UserMenu';
import { forgeApi } from '@/api/forgeApi';
import { useQueueStore, useQueueChannel } from '@/realtime/useQueueStore';
import type { Crew, SalaryTodaySummary } from '@/types';

interface TopBarProps {
  section: string;
  current: string;
}

// Verbatim port of the prototype's <header class="topbar">.
// DOM structure, classNames, and inline SVGs are kept identical.
// Phase 8.8 — the breadcrumb's "current" segment is replaced by a
// product <select> so the operator can scope every view at the top.
export function TopBar({ section, current }: TopBarProps) {
  const theme = useThemeStore((s) => s.theme);
  const toggle = useThemeStore((s) => s.toggle);
  const products = useProductsStore((s) => s.products);
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const loadProducts = useProductsStore((s) => s.load);
  const setActive = useProductsStore((s) => s.setActive);
  const me = useCurrentUser();
  const loadMe = useCurrentUserStore((s) => s.load);
  const avatarRef = useRef<HTMLDivElement | null>(null);
  const [menuOpen, setMenuOpen] = useState(false);
  const [menuAnchor, setMenuAnchor] = useState<DOMRect | null>(null);

  useEffect(() => { void loadProducts(); }, [loadProducts]);
  useEffect(() => { void loadMe(); }, [loadMe]);

  // Phase 11.9 / 14.11 — TopBar spend + payroll chips (PO only). Refresh
  // every 60 s so the operator sees both tick up live without hammering
  // the API. Q14.7: the legacy "salary" payload is now labelled "Spend"
  // (token-cost rollup); the new "Payroll" chip shows HR salary
  // (level × on-shift hours).
  const isPO = useHasRole('product-owner');
  const [salary, setSalary] = useState<SalaryTodaySummary | null>(null);
  const [payroll, setPayroll] = useState<{ totalUsd: number; crewCount: number } | null>(null);
  useEffect(() => {
    if (!isPO) return;
    let cancelled = false;
    const tick = () => {
      forgeApi.getSalaryTodayTotal()
        .then((s) => { if (!cancelled) setSalary(s); })
        .catch(() => { /* keep last value */ });
      forgeApi.getPayrollTodayTotal()
        .then((p) => { if (!cancelled) setPayroll(p); })
        .catch(() => { /* keep last value */ });
    };
    tick();
    // Payroll/spend change by minutes per minute — 5 min cadence is plenty.
    const id = window.setInterval(tick, 300_000);
    return () => { cancelled = true; window.clearInterval(id); };
  }, [isPO]);

  // Phase 17.8 — inbox chip. Subscribes to the queue's user channel and
  // counts pending acks for crews this user leads. Visible whenever the
  // signed-in user leads at least one crew (drops to zero otherwise).
  useQueueChannel({ userGroup: me?.id ?? null });
  const queueRows = useQueueStore((s) => s.rows);
  const refreshQueue = useQueueStore((s) => s.refresh);
  const [crews, setCrews] = useState<Crew[]>([]);
  useEffect(() => {
    let cancelled = false;
    void refreshQueue();
    void forgeApi.listCrews()
      .then((cs) => { if (!cancelled) setCrews(cs); })
      .catch(() => { /* offline */ });
    return () => { cancelled = true; };
  }, [refreshQueue]);
  const myLeadIds = useMemo(() => {
    if (!me) return new Set<string>();
    const id = (me.id ?? '').toLowerCase();
    const login = (me.login ?? '').toLowerCase();
    const display = (me.displayName ?? '').toLowerCase();
    return new Set(crews
      .filter((c) => {
        const lead = (c.lead ?? '').toLowerCase();
        return !!lead && (lead === id || lead === login || lead === display);
      })
      .map((c) => c.id));
  }, [crews, me]);
  const myTeamIds = useMemo(() => {
    return new Set(crews
      .filter((c) => myLeadIds.has(c.id))
      .map((c) => teamFromCrewName(c.name))
      .filter((t): t is string => !!t));
  }, [crews, myLeadIds]);
  const pendingAckCount = useMemo(() => {
    if (myLeadIds.size === 0) return 0;
    let n = 0;
    for (const r of Object.values(queueRows)) {
      if (r.state !== 'awaiting-ack') continue;
      if (r.targetKind === 'team' && myTeamIds.has(r.targetId)) n++;
      else if (r.targetKind === 'crew' && myLeadIds.has(r.targetId)) n++;
    }
    return n;
  }, [queueRows, myLeadIds, myTeamIds]);
  const navigate = useNavigate();
  const showInboxChip = myLeadIds.size > 0;

  function toggleMenu() {
    if (avatarRef.current) setMenuAnchor(avatarRef.current.getBoundingClientRect());
    setMenuOpen((o) => !o);
  }

  return (
    <header className="topbar">
      <div className="brand">
        <svg className="logomark" width="20" height="20" viewBox="0 0 20 20" fill="none">
          <rect x="1" y="1" width="18" height="18" rx="3" stroke="currentColor" strokeWidth="1.5" />
          <rect x="6.5" y="6.5" width="7" height="7" transform="rotate(45 10 10)" fill="currentColor" />
        </svg>
        <span className="brand-name">Forge</span>
      </div>

      <div className="topbar-context">
        <div className="breadcrumb-top">
          <span id="bc-section">{section}</span>
          <span className="sep">/</span>
          {products.length > 0 ? (
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
              {(() => {
                const active = products.find((p) => p.id === activeProductId) ?? products[0];
                return active ? <ProductIcon product={active} size={20} /> : null;
              })()}
              <select
                className="topbar-product-switch current"
                id="bc-current"
                value={activeProductId ?? products[0]?.id ?? ''}
                onChange={(e) => setActive(e.target.value)}
                title="Active product — every view is scoped to this"
                style={{
                  background: 'transparent',
                  color: 'inherit',
                  border: 'none',
                  font: 'inherit',
                  cursor: 'pointer',
                  padding: 0,
                }}
              >
                {products.map((p) => (
                  <option key={p.id} value={p.id}>{p.name}</option>
                ))}
              </select>
            </span>
          ) : (
            <span className="current" id="bc-current">{current}</span>
          )}
        </div>
      </div>

      <div className="topbar-spacer"></div>

      <div className="global-search">
        <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
          <circle cx="5.5" cy="5.5" r="3.5" stroke="currentColor" strokeWidth="1.3" />
          <line x1="8.4" y1="8.4" x2="10.5" y2="10.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
        </svg>
        <span>Search anything</span>
        <span className="kbd">⌘K</span>
      </div>

      {isPO && salary && (
        <div
          className="topbar-salary-chip"
          title={`Token-cost spend today across all crews. ${salary.activeAgents} active invocation${salary.activeAgents === 1 ? '' : 's'}.`}
          style={chipStyle('rgb(20, 110, 245)')}
        >
          <span style={{ color: 'var(--text-tertiary)', fontSize: 9.5, fontFamily: 'var(--font-mono, monospace)', textTransform: 'uppercase', letterSpacing: 0.1 }}>spend</span>
          <span style={{ color: 'rgb(20, 110, 245)', fontWeight: 600 }}>${salary.totalUsd.toFixed(2)}</span>
          <span style={{ opacity: 0.7 }}>today</span>
          {salary.activeAgents > 0 && (
            <span style={{
              width: 6, height: 6, borderRadius: 999,
              background: 'rgb(20, 110, 245)',
              boxShadow: '0 0 0 2px color-mix(in srgb, rgb(20, 110, 245) 25%, transparent)',
            }} />
          )}
        </div>
      )}
      {isPO && payroll && (
        <div
          className="topbar-payroll-chip"
          title={`HR payroll today across ${payroll.crewCount} crew${payroll.crewCount === 1 ? '' : 's'} (level × on-shift hours).`}
          style={chipStyle('rgb(34, 197, 94)')}
        >
          <span style={{ color: 'var(--text-tertiary)', fontSize: 9.5, fontFamily: 'var(--font-mono, monospace)', textTransform: 'uppercase', letterSpacing: 0.1 }}>payroll</span>
          <span style={{ color: 'rgb(34, 197, 94)', fontWeight: 600 }}>${payroll.totalUsd.toFixed(2)}</span>
          <span style={{ opacity: 0.7 }}>today</span>
        </div>
      )}
      {/* Phase 17.8 — inbox chip for crew leads. */}
      {showInboxChip && (
        <button
          type="button"
          className="topbar-inbox-chip"
          onClick={() => navigate('/inbox')}
          title={pendingAckCount === 0 ? 'Inbox · no pending acks' : `Inbox · ${pendingAckCount} pending ack${pendingAckCount === 1 ? '' : 's'}`}
          style={{
            ...chipStyle(pendingAckCount > 0 ? 'var(--warning)' : 'var(--text-tertiary)'),
            cursor: 'pointer',
            border: 'none',
            background: 'transparent',
          }}
        >
          <span style={{ fontSize: 12 }} aria-hidden>📥</span>
          <span style={{
            color: pendingAckCount > 0 ? 'var(--warning)' : 'var(--text-tertiary)',
            fontWeight: pendingAckCount > 0 ? 600 : 500,
            fontVariantNumeric: 'tabular-nums',
          }}>
            {pendingAckCount}
          </span>
          {pendingAckCount > 0 && (
            <span style={{
              width: 6, height: 6, borderRadius: 999,
              background: 'var(--warning)',
              animation: 'forge-pulse 1.6s ease-in-out infinite',
            }} />
          )}
        </button>
      )}

      <button className="icon-btn" id="theme-toggle" title="Toggle theme" onClick={toggle}>
        <svg id="moon-icon" width="16" height="16" viewBox="0 0 16 16" fill="none" style={{ display: theme === 'dark' ? 'block' : 'none' }}>
          <path d="M13.5 9.5 A 6 6 0 1 1 6.5 2.5 A 5 5 0 0 0 13.5 9.5 Z" stroke="currentColor" strokeWidth="1.4" strokeLinejoin="round" />
        </svg>
        <svg id="sun-icon" width="16" height="16" viewBox="0 0 16 16" fill="none" style={{ display: theme === 'dark' ? 'none' : 'block' }}>
          <circle cx="8" cy="8" r="3.2" stroke="currentColor" strokeWidth="1.4" />
          <path d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.4 3.4l1.4 1.4M11.2 11.2l1.4 1.4M3.4 12.6l1.4-1.4M11.2 4.8l1.4-1.4" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
        </svg>
      </button>

      <button className="icon-btn" title="Notifications">
        <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
          <path d="M5.5 11.5 a 4.5 4.5 0 0 1 5 0 V 5.5 a 2.5 2.5 0 0 0 -5 0 Z M 6.5 13.5 a 1.5 1 0 0 0 3 0" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" fill="none" />
        </svg>
        <span className="badge-dot"></span>
      </button>

      <div
        ref={avatarRef}
        className="avatar"
        title={`${me.displayName}${me.email ? ' · ' + me.email : ''}`}
        onClick={toggleMenu}
        style={{
          cursor: 'pointer',
          ...(me.avatarUrl ? { backgroundImage: `url(${me.avatarUrl})`, backgroundSize: 'cover' } : {}),
        }}
      >
        {me.avatarUrl ? '' : me.initials}
      </div>
      <UserMenu open={menuOpen} onClose={() => setMenuOpen(false)} anchor={menuAnchor} />
    </header>
  );
}

/** Phase 14.11 — shared TopBar chip shape for spend + payroll. */
function chipStyle(accent: string): React.CSSProperties {
  return {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 6,
    padding: '4px 10px',
    borderRadius: 999,
    border: `1px solid ${accent}`,
    color: 'var(--text-secondary)',
    fontSize: 11.5,
    fontFamily: 'var(--font-mono, monospace)',
    background: 'var(--bg-elevated, transparent)',
  };
}

function teamFromCrewName(name: string): string | null {
  const n = (name ?? '').toLowerCase();
  if (n.startsWith('arch')) return 'Architecture';
  if (n.startsWith('eng')) return 'Engineering';
  if (n.startsWith('rev')) return 'Review';
  if (n.startsWith('qa')) return 'QA';
  return null;
}
