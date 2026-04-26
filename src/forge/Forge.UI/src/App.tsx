import { useEffect } from 'react';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { AppShell } from '@/shell/AppShell';
import { OverviewView } from '@/views/OverviewView';
import { PlanView } from '@/views/PlanView';
import { TimelineView } from '@/views/TimelineView';
import { WorkstreamsView } from '@/views/WorkstreamsView';
import { PrsView } from '@/views/PrsView';
import { SettingsView } from '@/views/SettingsView';
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
          <Route path="/timeline" element={<TimelineView />} />
          <Route path="/workstreams" element={<WorkstreamsView />} />
          <Route path="/prs" element={<PrsView />} />
          <Route path="/settings" element={<SettingsView />} />
          <Route path="*" element={<Navigate to="/plan" replace />} />
        </Route>
      </Routes>
    </BrowserRouter>
  );
}
