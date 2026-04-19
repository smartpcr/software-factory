import React from 'react';
import './AppShell.css';

export interface AppShellProps {
  topbar?: React.ReactNode;
  palette?: React.ReactNode;
  canvas?: React.ReactNode;
  inspector?: React.ReactNode;
  logs?: React.ReactNode;
}

export function AppShell({ topbar, palette, canvas, inspector, logs }: AppShellProps) {
  return (
    <div className="app-shell" data-testid="app-shell">
      <header className="app-shell__topbar" data-testid="app-topbar">
        {topbar}
      </header>
      <aside className="app-shell__palette" data-testid="app-palette">
        {palette}
      </aside>
      <main className="app-shell__canvas" data-testid="app-canvas">
        {canvas}
      </main>
      <aside className="app-shell__inspector" data-testid="app-inspector">
        {inspector}
      </aside>
      <section className="app-shell__logs" data-testid="app-logs">
        {logs}
      </section>
    </div>
  );
}
