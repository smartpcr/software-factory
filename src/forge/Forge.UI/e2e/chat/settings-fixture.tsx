import React from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Link, Route, Routes } from 'react-router-dom';
import { SettingsView } from '../../src/views/SettingsView';
import '../../src/styles/index.css';

// Use the actual full-page view and shell geometry: the smaller Chat fixture
// cannot expose competing scroll containers in SettingsView.
function Fixture() {
  return <div className="app sidebar-collapsed">
    <header className="topbar">Forge settings exercise</header><aside className="sidebar" />
    <main className="main"><Routes>
      <Route path="/workstreams" element={<><h1>Workstreams fixture</h1><Link to="/e2e/chat/settings-fixture.html">Open Settings</Link></>} />
      <Route path="*" element={<SettingsView />} />
    </Routes></main>
  </div>;
}
createRoot(document.getElementById('root')!).render(<BrowserRouter><Fixture /></BrowserRouter>);