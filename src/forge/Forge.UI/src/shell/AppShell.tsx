import { useEffect, useMemo } from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import { TopBar } from './TopBar';
import { SideBar } from './SideBar';
import { CommandPalette } from './CommandPalette';
import { KeyboardMap } from './KeyboardMap';
import { ErrorBoundary } from './ErrorBoundary';
import { useSidebarStore } from '@/state/useSidebarStore';
import { useProductsStore } from '@/state/useProductsStore';
import { useProductFavicon } from '@/products/useProductFavicon';
import { logView } from '@/telemetry';

// Maps each route to the breadcrumb labels the prototype shows in the topbar.
const breadcrumbs: Record<string, { section: string; current: string }> = {
  '/':            { section: 'Overview',       current: 'Atlas factory floor' },
  '/plan':        { section: 'Plan & Design',  current: 'Sprint 14 Board' },
  '/timeline':    { section: 'Timeline',       current: 'Feature & branch flow' },
  '/workstreams': { section: 'Workstreams',    current: 'Parallel workstreams' },
  '/prs':         { section: 'Pull Requests',  current: 'Pull request flow' },
  '/products':    { section: 'Products',       current: 'Products & repos' },
};

export function AppShell() {
  const collapsed = useSidebarStore((s) => s.collapsed);
  const { pathname } = useLocation();
  const crumbs = breadcrumbs[pathname] ?? { section: 'Forge', current: '' };

  // Phase 8 — drive the favicon + window title from the active product.
  const products = useProductsStore((s) => s.products);
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const activeProduct = useMemo(
    () => products.find((p) => p.id === activeProductId) ?? products[0] ?? null,
    [products, activeProductId],
  );
  useProductFavicon(activeProduct);

  useEffect(() => { logView(pathname); }, [pathname]);

  return (
    <div className={`app${collapsed ? ' sidebar-collapsed' : ''}`} id="app">
      <TopBar section={crumbs.section} current={crumbs.current} />
      <SideBar />
      <main className="main">
        <ErrorBoundary name={crumbs.section}>
          <Outlet />
        </ErrorBoundary>
      </main>
      <CommandPalette />
      <KeyboardMap />
    </div>
  );
}
