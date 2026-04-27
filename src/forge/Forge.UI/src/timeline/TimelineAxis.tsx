interface AxisProps {
  startDay: number;
  endDay: number;
  dayW: number;
}

const DAY_NAMES = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

// Verbatim port of the prototype's axis-building loop (lines 3691-3709).
export function TimelineAxis({ startDay, endDay, dayW }: AxisProps) {
  const totalDays = endDay - startDay;
  const trackW = totalDays * dayW;
  const today = new Date();

  const days: { idx: number; name: string; num: number; weekend: boolean; today: boolean }[] = [];
  for (let d = startDay; d < endDay; d++) {
    const date = new Date(today);
    date.setDate(today.getDate() + d);
    const name = DAY_NAMES[date.getDay()];
    days.push({
      idx: d,
      name,
      num: date.getDate(),
      weekend: name === 'Sat' || name === 'Sun',
      today: d === 0,
    });
  }

  return (
    <div className="timeline-axis">
      <div className="timeline-axis-label">
        <span>WORKSTREAM</span>
        <span style={{ opacity: 0.5 }}>{totalDays}d window</span>
      </div>
      <div
        className="timeline-axis-days"
        style={{ gridTemplateColumns: `repeat(${totalDays}, ${dayW}px)`, minWidth: `${trackW}px` }}
      >
        {days.map((d) => (
          <div
            key={d.idx}
            className={[
              'timeline-axis-day',
              d.weekend ? 'weekend' : '',
              d.today ? 'today' : '',
            ].filter(Boolean).join(' ')}
          >
            {d.name}<span className="num">{d.num}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
