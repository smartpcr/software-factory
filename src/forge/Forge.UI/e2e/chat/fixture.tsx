import React from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter } from 'react-router-dom';
import { SwarmStoryRow } from '../../src/swarm/SwarmStoryRow';
import type { StorySpawn } from '../../src/api/forgeApi';
import '../../src/styles/index.css';

const spawn = (productId: string, title: string): StorySpawn => ({
  id: 'shared', productId, storyId: 'story-' + productId, storyTitle: title, repoId: 'repo-' + productId,
  phase: 'implement', baseBranch: 'main', createdAt: '2026-09-10T00:00:00Z', updatedAt: '2026-09-10T00:00:00Z',
} as StorySpawn);
createRoot(document.getElementById('root')!).render(<MemoryRouter><main className="swarm-ctrl" style={{ padding: 32 }}>
  <h1 style={{ fontSize: 24, marginBottom: 24 }}>Active stories</h1><div className="story-list">
    <SwarmStoryRow spawn={spawn('p', 'Build the Telegram notification slice')} index={0} />
    <SwarmStoryRow spawn={spawn('q', 'Another product, same spawn ID')} index={1} />
  </div>
</main></MemoryRouter>);
