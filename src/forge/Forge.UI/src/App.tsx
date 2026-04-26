import { useEffect } from 'react';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { AppShell } from '@/shell/AppShell';
import { OverviewView } from '@/views/OverviewView';
import { PlanView } from '@/views/PlanView';
import { TimelineView } from '@/views/TimelineView';
import { WorkstreamsView } from '@/views/WorkstreamsView';
import { PrsView } from '@/views/PrsView';
import { SettingsView } from '@/views/SettingsView';
import { AuditView } from '@/views/AuditView';
import { SwarmView } from '@/views/SwarmView';
import { PoliciesView } from '@/views/PoliciesView';
import { CostView } from '@/views/CostView';
import { ProductsView } from '@/views/ProductsView';
import { CrewsView } from '@/views/CrewsView';
import { WorkItemsView } from '@/views/WorkItemsView';
import { useStateStore } from '@/state/useStateStore';

export function App() {
  const load = useStateStore((s) => s.load);

  // Phase 0: prime the store so Phases 1+ can render off it without re-fetching.
  useEffect(() => {
    void load();
  }, [load]);

  return (
    <BrowserRouter>
      <Routes>
        <Route element={<AppShell />}>
          <Route path="/" element={<OverviewView />} />
          <Route path="/plan" element={<PlanView />} />
          <Route path="/work-items" element={<WorkItemsView />} />
          <Route path="/timeline" element={<TimelineView />} />
          <Route path="/workstreams" element={<WorkstreamsView />} />
          <Route path="/prs" element={<PrsView />} />
          <Route path="/swarm" element={<SwarmView />} />
          <Route path="/policies" element={<PoliciesView />} />
          <Route path="/cost" element={<CostView />} />
          <Route path="/products" element={<ProductsView />} />
          <Route path="/crews" element={<CrewsView />} />
          <Route path="/settings" element={<SettingsView />} />
          <Route path="/audit" element={<AuditView />} />
          <Route path="*" element={<Navigate to="/plan" replace />} />
        </Route>
      </Routes>
    </BrowserRouter>
  );
}
