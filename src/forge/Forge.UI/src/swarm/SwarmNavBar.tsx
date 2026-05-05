import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';

export interface DropdownOption {
  label: string;
  value: string;
  active?: boolean;
}

export interface CrumbSpec {
  /** small all-caps prefix shown above the label, e.g. "MODE" */
  pre?: string;
  label: string;
  href?: string;
  active?: boolean;
  /** When set, clicking this crumb opens a dropdown to switch values. */
  dropdown?: DropdownOption[];
  onDropdownSelect?: (value: string) => void;
}

interface Props {
  crumbs: CrumbSpec[];
  /** show a back button on the right */
  onBack?: () => void;
  /** show "OVERVIEW" home button on the right */
  homeHref?: string;
}

export function SwarmNavBar({ crumbs, onBack, homeHref = '/workstreams' }: Props) {
  const navigate = useNavigate();

  return (
    <nav className="nav-bar">
      <div className="crumbs">
        {crumbs.map((c, i) => (
          <span key={i} style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
            {i > 0 && <span className="crumb-sep">/</span>}
            {c.dropdown && c.onDropdownSelect ? (
              <CrumbDropdown crumb={c} />
            ) : (
              <a
                className={`crumb${c.active ? ' active' : ''}`}
                onClick={(e) => {
                  e.preventDefault();
                  if (c.href) navigate(c.href);
                }}
                href={c.href}
              >
                {c.pre && <span className="pre">{c.pre}</span>}
                <span>{c.label}</span>
              </a>
            )}
          </span>
        ))}
      </div>
      <div className="nav-ops">
        {onBack && <button className="nav-btn" onClick={onBack}>◂ BACK</button>}
        <button className="nav-btn go" onClick={() => navigate(homeHref)}>⌂ OVERVIEW</button>
      </div>
    </nav>
  );
}

function CrumbDropdown({ crumb }: { crumb: CrumbSpec }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    function handleClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    if (open) document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [open]);

  return (
    <div ref={ref} style={{ position: 'relative', display: 'inline-flex' }}>
      <a
        className="crumb active"
        onClick={(e) => { e.preventDefault(); setOpen(!open); }}
        href="#"
        style={{ cursor: 'pointer' }}
      >
        {crumb.pre && <span className="pre">{crumb.pre}</span>}
        <span>{crumb.label} ▾</span>
      </a>
      {open && (
        <div style={{
          position: 'absolute',
          top: '100%',
          left: 0,
          zIndex: 200,
          background: 'var(--sc-surface-2, var(--bg-elevated, #1e1e2e))',
          border: '1px solid var(--sc-border, var(--border, #444))',
          borderRadius: 6,
          padding: '6px 0',
          minWidth: 180,
          boxShadow: '0 4px 16px rgba(0,0,0,0.3)',
        }}>
          {crumb.dropdown!.map((opt) => (
            <button
              key={opt.value}
              type="button"
              onClick={() => { crumb.onDropdownSelect!(opt.value); setOpen(false); }}
              style={{
                display: 'block',
                width: '100%',
                textAlign: 'left',
                padding: '6px 14px',
                background: opt.active ? 'var(--accent-bg, rgba(79,140,255,0.15))' : 'transparent',
                border: 'none',
                color: opt.active ? 'var(--accent, #4f8cff)' : 'var(--sc-text, var(--text-primary, #eee))',
                fontSize: 12,
                cursor: 'pointer',
                fontFamily: 'inherit',
              }}
            >
              {opt.active && '● '}{opt.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
