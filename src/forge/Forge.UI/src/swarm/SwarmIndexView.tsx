import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { forgeApi, type StorySpawn, type StorySpawnPhase } from '@/api/forgeApi';
import { SwarmTopbar } from './SwarmTopbar';
import { SwarmNavBar } from './SwarmNavBar';
import { SwarmStoryRow } from './SwarmStoryRow';
import { SpawnStoryModal } from '@/workstreams/SpawnStoryModal';
import { Toaster } from '@/board/Toaster';

type PhaseFilter = 'all' | StorySpawnPhase;

const FILTER_CHIPS: { key: PhaseFilter; label: string }[] = [
  { key: 'all',       label: 'All' },
  { key: 'plan',      label: 'Plan' },
  { key: 'decompose', label: 'Decompose' },
  { key: 'implement', label: 'Implement' },
  { key: 'review',    label: 'Review' },
  { key: 'qa',        label: 'QA' },
  { key: 'done',      label: 'Done' },
];

export function SwarmIndexView() {
  const navigate = useNavigate();
  const [spawns, setSpawns] = useState<StorySpawn[]>([]);
  const [filter, setFilter] = useState<PhaseFilter>('all');
  const [search, setSearch] = useState('');
  const [spawnModalOpen, setSpawnModalOpen] = useState(false);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const rows = await forgeApi.listStorySpawns();
        if (!cancelled) setSpawns(rows);
      } catch {
        if (!cancelled) setSpawns([]);
      }
    }
    void load();
    const id = setInterval(() => { void load(); }, 15_000);
    return () => { cancelled = true; clearInterval(id); };
  }, []);

  const counts = useMemo(() => {
    const c: Record<PhaseFilter, number> = { all: spawns.length, plan: 0, decompose: 0, implement: 0, review: 0, qa: 0, done: 0 };
    for (const s of spawns) c[s.phase]++;
    return c;
  }, [spawns]);

  const filtered = useMemo(() => {
    let next = spawns.slice();
    if (filter !== 'all') next = next.filter((s) => s.phase === filter);
    if (search.trim()) {
      const q = search.toLowerCase();
      next = next.filter((s) => s.storyTitle.toLowerCase().includes(q) || s.storyId.toLowerCase().includes(q));
    }
    return next;
  }, [spawns, filter, search]);

  const meters = useMemo(() => {
    const totalPhases = spawns.length * 6;
    const advancedPhases = spawns.reduce((acc, s) => {
      const idx = (['plan', 'decompose', 'implement', 'review', 'qa', 'done'] as StorySpawnPhase[]).indexOf(s.phase);
      return acc + idx + 1;
    }, 0);
    const pct = totalPhases > 0 ? Math.round((advancedPhases / totalPhases) * 100) : 0;
    return [
      { k: 'Stories',        v: String(spawns.length),     delta: `${counts.implement} active`, tone: undefined },
      { k: 'In Plan',        v: String(counts.plan),       delta: `${counts.decompose} decomposing`, tone: 'amber' as const },
      { k: 'Implementing',   v: String(counts.implement),  delta: `${counts.review} in review`, tone: 'cyan' as const },
      { k: 'Done',           v: String(counts.done),       delta: 'complete', tone: 'green' as const, deltaTone: 'up' as const },
      { k: 'Pipeline',       v: `${pct}%`,                 delta: 'across all stories' },
      { k: 'QA',             v: String(counts.qa),         delta: 'in test', tone: 'green' as const },
    ];
  }, [spawns, counts]);

  return (
    <div className="swarm-ctrl">
      <div className="shell">
        <SwarmTopbar
          eyebrow="SWARM CONTROL // STORY INDEX"
          title="PARALLEL STORY DELIVERY"
          subtitle={`${spawns.length} active spawns · 4 crews × 20 agents · agent-driven lifecycle`}
          meters={meters}
        />
        <SwarmNavBar
          crumbs={[
            { pre: 'ROOT', label: 'Workstreams', href: '/workstreams', active: true },
          ]}
          homeHref="/"
        />

        <div className="index-toolbar">
          <div className="filters">
            {FILTER_CHIPS.map((chip) => (
              <button
                key={chip.key}
                className={`pill${filter === chip.key ? ' active' : ''}`}
                onClick={() => setFilter(chip.key)}
              >
                {chip.label} <span className="ct">{counts[chip.key]}</span>
              </button>
            ))}
            <input
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search stories…"
              style={{
                marginLeft: 8,
                background: 'var(--sc-bg-2)',
                border: '1px solid var(--sc-border)',
                color: 'var(--sc-text)',
                padding: '5px 10px',
                fontFamily: 'var(--sc-font-mono)',
                fontSize: 11,
                outline: 'none',
                minWidth: 220,
              }}
            />
          </div>
          <div className="actions">
            <button className="btn" onClick={() => navigate('/work-items')}>▌ WORK ITEMS</button>
            <button className="btn primary" onClick={() => setSpawnModalOpen(true)}>+ SPAWN STORY</button>
          </div>
        </div>

        <div className="index">
          {filtered.length === 0 ? (
            <div className="empty">
              <div className="lbl">▌ NO STORIES { filter !== 'all' ? `IN ${filter.toUpperCase()}` : '' }</div>
              <div className="sub">
                {filter === 'all'
                  ? 'Spawn a story from an ADO Feature / PBI to start a multi-team delivery — Forge orchestrates plan → decompose → implement → review → qa across the four crews.'
                  : `No spawns in ${filter} phase right now.`}
              </div>
              {filter === 'all' && (
                <button className="btn primary" onClick={() => setSpawnModalOpen(true)}>+ SPAWN FIRST STORY</button>
              )}
            </div>
          ) : (
            <div className="story-list">
              {filtered.map((s, i) => (
                <SwarmStoryRow key={s.storyId} spawn={s} index={i} />
              ))}
            </div>
          )}
        </div>
      </div>

      <SpawnStoryModal
        open={spawnModalOpen}
        onClose={() => setSpawnModalOpen(false)}
        onSpawned={(storyId) => {
          setSpawnModalOpen(false);
          navigate(`/workstreams/${encodeURIComponent(storyId)}`);
        }}
      />
      <Toaster />
    </div>
  );
}
