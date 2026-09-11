import React, { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter } from 'react-router-dom';
import { SwarmStoryRow } from '../../src/swarm/SwarmStoryRow';
import { ChatRowControl } from '../../src/swarm/plugins/ChatRowControl';
import { ChatIntegrations } from '../../src/swarm/plugins/ChatIntegrations';
import type { StorySpawn } from '../../src/api/forgeApi';
import '../../src/styles/index.css';

const titles = ['BEYOND-AST', '3D-RENDER', 'MCP', 'AGENT MEMORY', 'PR INGESTER'];
function Fixture() {
  const [product, setProduct] = useState('p');
  const [reverse, setReverse] = useState(false);
  const [filter, setFilter] = useState('');
  const [settings, setSettings] = useState(false);
  const [detail, setDetail] = useState(false);
  const rows = titles.map((title, index) => ({ id: 'row-' + index, productId: product, storyId: 'story-' + index, storyTitle: title,
    repoId: 'repo-' + product, wsTotal: index === 0 ? 32 : 0, phase: index === 2 ? 'done' : 'implement', baseBranch: 'main', createdAt: '2026-09-10T00:00:00Z', updatedAt: '2026-09-10T00:00:00Z' } as StorySpawn));
  if (reverse) rows.reverse();
  return <main className="swarm-ctrl" style={{ padding: 32, height: 'auto', minHeight: '100vh' }}>
    <h1>Active stories</h1><nav style={{ display: 'flex', gap: 12, marginBottom: 24 }}>
      <label>Product<select value={product} onChange={e => setProduct(e.target.value)}><option>p</option><option>q</option></select></label>
      <button onClick={() => setReverse(v => !v)}>Reverse rows</button><label>Filter rows<input value={filter} onChange={e => setFilter(e.target.value)} /></label>
      <button onClick={() => setSettings(v => !v)}>Chat settings</button><button onClick={() => setDetail(v => !v)}>Detail header</button>
    </nav>
    {settings ? <ChatIntegrations /> : <>
      {detail && <header aria-label="Story detail header"><h2>BEYOND-AST</h2><ChatRowControl productId={product} spawnId="row-0" title="BEYOND-AST detail" /></header>}
      <div className="story-list">{rows.filter(r => r.storyTitle.includes(filter)).map((row, i) => <SwarmStoryRow key={`${product}:${row.id}`} spawn={row} index={i} />)}</div>
    </>}
  </main>;
}
createRoot(document.getElementById('root')!).render(<MemoryRouter><Fixture /></MemoryRouter>);
