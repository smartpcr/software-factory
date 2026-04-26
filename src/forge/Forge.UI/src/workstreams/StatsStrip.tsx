import type { WsStat } from '@/types';

// Verbatim port of the prototype's stats-strip block (lines 3808-3814).
export function StatsStrip({ stats }: { stats: WsStat[] }) {
  return (
    <div className="workstreams-stats" id="workstreams-stats">
      {stats.map((s, i) => (
        <div className="stat-cell" key={`${s.label}-${i}`}>
          <div className="stat-cell-label">{s.label}</div>
          <div className="stat-cell-val">{s.val}<span className="unit">{s.unit}</span></div>
          <div className="stat-cell-trend">
            <span className={s.trend === 'up' ? 'up' : s.trend === 'down' ? 'down' : ''}>
              {s.trend === 'up' ? '↑' : s.trend === 'down' ? '↓' : '·'}
            </span>{' '}{s.trendVal}
          </div>
        </div>
      ))}
    </div>
  );
}
