// Verbatim port of the prototype's legend (lines 3782-3796).
export function TimelineLegend() {
  return (
    <div className="timeline-legend">
      <span className="timeline-legend-item"><span className="timeline-legend-swatch" style={{ background: 'var(--epic-024)' }} />EPIC-024</span>
      <span className="timeline-legend-item"><span className="timeline-legend-swatch" style={{ background: 'var(--epic-021)' }} />EPIC-021</span>
      <span className="timeline-legend-item"><span className="timeline-legend-swatch" style={{ background: 'var(--epic-022)' }} />EPIC-022</span>
      <span className="timeline-legend-item"><span className="timeline-legend-swatch" style={{ background: 'var(--epic-026)' }} />EPIC-026</span>
      <span className="timeline-legend-item"><span className="timeline-legend-swatch" style={{ background: 'var(--epic-018)' }} />EPIC-018</span>
      <span style={{ opacity: 0.4 }}>|</span>
      <span className="timeline-legend-item"><span className="timeline-legend-swatch diamond" style={{ background: 'var(--info)' }} />PR opened</span>
      <span className="timeline-legend-item"><span className="timeline-legend-swatch diamond" style={{ background: 'var(--warning)' }} />Review / gate</span>
      <span className="timeline-legend-item"><span className="timeline-legend-swatch diamond" style={{ background: 'var(--success)' }} />Merge</span>
      <span className="timeline-legend-item"><span className="timeline-legend-swatch diamond" style={{ background: 'var(--danger)' }} />Conflict</span>
    </div>
  );
}
