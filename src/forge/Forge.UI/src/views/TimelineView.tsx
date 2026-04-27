import { useEffect, useMemo, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import type { TimelineBar, TimelineFilter, TimelineZoom } from '@/types';
import { useStateStore } from '@/state/useStateStore';
import { useTimelineStore, ZOOM_DAY_W } from '@/state/useTimelineStore';
import { useToastStore } from '@/state/useToastStore';
import { sampleState } from '@/sampleData';
import { Timeline } from '@/timeline/Timeline';
import { Toaster } from '@/board/Toaster';

const ZOOM_BUTTONS: TimelineZoom[] = ['day', 'week', 'sprint'];
const FILTER_CHIPS: Array<{ key: TimelineFilter; label: string }> = [
  { key: 'all',       label: 'All epics' },
  { key: 'active',    label: 'Active' },
  { key: 'merging',   label: 'Merging' },
  { key: 'planning',  label: 'Planning' },
];

export function TimelineView() {
  const apiState = useStateStore((s) => s.state);
  const epics = apiState?.epics ?? sampleState.epics;

  const data = useTimelineStore((s) => s.data);
  const load = useTimelineStore((s) => s.load);
  const zoom = useTimelineStore((s) => s.zoom);
  const setZoom = useTimelineStore((s) => s.setZoom);
  const forecast = useTimelineStore((s) => s.forecast);
  const toggleForecast = useTimelineStore((s) => s.toggleForecast);
  const lane = useTimelineStore((s) => s.lane);
  const setLane = useTimelineStore((s) => s.setLane);
  const filter = useTimelineStore((s) => s.filter);
  const setFilter = useTimelineStore((s) => s.setFilter);
  const collapsedEpics = useTimelineStore((s) => s.collapsedEpics);
  const toggleEpic = useTimelineStore((s) => s.toggleEpic);

  const navigate = useNavigate();
  const wrapRef = useRef<HTMLDivElement>(null);
  const timelineRef = useRef<HTMLDivElement>(null);

  // Phase 2.11 — telemetry: time-on-view + open count.
  useEffect(() => {
    const t0 = performance.now();
    console.info('forge.timeline.opened', { ts: Date.now() });
    return () => {
      const ms = Math.round(performance.now() - t0);
      console.info('forge.timeline.closed', { durationMs: ms });
    };
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const startDay = data?.from ?? -10;
  const endDay = data?.to ?? 5;
  const dayW = ZOOM_DAY_W[zoom];

  const filteredBars = useMemo(() => {
    const bars = data?.bars ?? [];
    if (filter === 'all') return bars;
    return bars.filter((b) => {
      switch (filter) {
        case 'active':   return b.status === 'active' || b.status === 'review';
        case 'merging':  return b.status === 'merging';
        case 'planning': return b.status === 'planning' || b.planned;
        default:         return true;
      }
    });
  }, [data, filter]);

  const filterCounts = useMemo(() => {
    const bars = data?.bars ?? [];
    return {
      all:      bars.length,
      active:   bars.filter((b) => b.status === 'active' || b.status === 'review').length,
      merging:  bars.filter((b) => b.status === 'merging').length,
      planning: bars.filter((b) => b.status === 'planning' || b.planned).length,
    };
  }, [data]);

  function scrollToNow() {
    const wrap = wrapRef.current;
    const tl = timelineRef.current;
    if (!wrap || !tl) return;
    const today = tl.querySelector<HTMLElement>('.timeline-today');
    if (!today) return;
    const headW = tl.querySelector<HTMLElement>('.timeline-axis-label')?.offsetWidth ?? 0;
    const target = today.offsetLeft - headW - wrap.clientWidth / 3;
    wrap.scrollTo({ left: Math.max(0, target), behavior: 'smooth' });
  }

  function onBarClick(bar: TimelineBar) {
    useToastStore.getState().show(`Filtered to ${bar.name}`, 'success');
    navigate('/workstreams');
  }

  return (
    <section className="view active" id="view-timeline">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">02</span>&nbsp;Timeline</span>
            <span className="sep">/</span>
            <span>{endDay - startDay}-day window</span>
            <span className="sep">/</span>
            <span><span className="live-dot" />{filteredBars.length} active branches</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Feature & branch flow
          </h1>
        </div>
        <div className="page-head-right">
          <button className="btn btn-secondary" onClick={scrollToNow}>
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
              <path d="M6 2 V 6 L 9 8" stroke="currentColor" strokeWidth="1.3" fill="none" strokeLinecap="round" />
              <circle cx="6" cy="6" r="4" stroke="currentColor" strokeWidth="1.3" fill="none" />
            </svg>
            Now
          </button>
          <button
            className={`btn btn-secondary${forecast ? ' active' : ''}`}
            onClick={toggleForecast}
            aria-pressed={forecast}
          >
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
              <path d="M2 6 H 10 M 7 3 L 10 6 L 7 9" stroke="currentColor" strokeWidth="1.3" fill="none" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            Forecast{forecast ? ' on' : ''}
          </button>
          <button className="btn btn-primary">Plan release</button>
        </div>
      </div>

      <div className="timeline-toolbar">
        <div className="filter-chips" style={{ borderRight: '1px solid var(--border)', paddingRight: 12 }}>
          {FILTER_CHIPS.map((chip) => (
            <button
              key={chip.key}
              className={`filter-chip${filter === chip.key ? ' active' : ''}`}
              onClick={() => setFilter(chip.key)}
            >
              {chip.label} <span className="count">{filterCounts[chip.key]}</span>
            </button>
          ))}
        </div>
        <div className="timeline-zoom">
          {ZOOM_BUTTONS.map((z) => (
            <button
              key={z}
              className={zoom === z ? 'active' : ''}
              data-zoom={z}
              onClick={() => setZoom(z)}
            >
              {z[0].toUpperCase() + z.slice(1)}
            </button>
          ))}
        </div>
        <button
          className="filter-select"
          onClick={() => setLane(lane === 'workstream' ? 'epic' : 'workstream')}
        >
          <span className="label">Lane</span>
          <span>{lane === 'workstream' ? 'Workstream' : 'Epic'}</span>
          <svg width="9" height="9" viewBox="0 0 9 9">
            <path d="M2 3.5 L4.5 6 L7 3.5" stroke="currentColor" fill="none" strokeWidth="1.3" strokeLinecap="round" />
          </svg>
        </button>
        <div className="filter-search" style={{ marginLeft: 'auto' }}>
          <svg width="12" height="12" viewBox="0 0 12 12" fill="none" style={{ color: 'var(--text-tertiary)' }}>
            <circle cx="5.5" cy="5.5" r="3.5" stroke="currentColor" strokeWidth="1.3" />
            <line x1="8.4" y1="8.4" x2="10.5" y2="10.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
          </svg>
          <input type="text" placeholder="Search branches…" />
        </div>
      </div>

      <div className="timeline-wrap" ref={wrapRef}>
        <Timeline
          ref={timelineRef}
          epics={epics}
          bars={filteredBars}
          markers={data?.markers ?? []}
          startDay={startDay}
          endDay={endDay}
          dayW={dayW}
          forecast={forecast}
          lane={lane}
          collapsedEpics={collapsedEpics as Set<string>}
          onToggleEpic={toggleEpic}
          onBarClick={onBarClick}
        />
      </div>

      <Toaster />
    </section>
  );
}
