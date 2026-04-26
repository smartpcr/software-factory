import { useEffect, useRef, useState } from 'react';
import { useThemeStore } from '@/state/useThemeStore';
import { useProductsStore } from '@/state/useProductsStore';
import { useCurrentUser, useCurrentUserStore } from '@/state/useCurrentUserStore';
import { ProductIcon } from '@/products/ProductIcon';
import { UserMenu } from '@/shell/UserMenu';

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
