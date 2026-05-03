import { useEffect, useState } from 'react';

interface MeterSpec {
  k: string;
  v: string;
  delta?: string;
  tone?: 'amber' | 'cyan' | 'green' | 'red';
  deltaTone?: 'up' | 'dn';
}

interface Props {
  /** "STORY-4471" — the small label above the title */
  eyebrow: string;
  title: string;
  subtitle?: string;
  meters: MeterSpec[];
  showClock?: boolean;
}

export function SwarmTopbar({ eyebrow, title, subtitle, meters, showClock = true }: Props) {
  const [now, setNow] = useState(() => new Date());

  useEffect(() => {
    if (!showClock) return;
    const id = setInterval(() => setNow(new Date()), 1000);
    return () => clearInterval(id);
  }, [showClock]);

  return (
    <header className="topbar">
      <div className="brand">
        <div className="brand-mark" />
        <div className="brand-text">
          <div className="lbl">{eyebrow}</div>
          <h1>{title}</h1>
          {subtitle && <div className="sub">{subtitle}</div>}
        </div>
      </div>

      <div className="meters">
        {meters.slice(0, 6).map((m, i) => (
          <div className="meter" key={i}>
            <span className="k">{m.k}</span>
            <span className={`v${m.tone ? ' ' + m.tone : ''}`}>{m.v}</span>
            {m.delta && <span className={`delta${m.deltaTone ? ' ' + m.deltaTone : ''}`}>{m.delta}</span>}
          </div>
        ))}
      </div>

      {showClock && (
        <div className="clock">
          <div className="cap">UTC · LIVE</div>
          <div className="now">{now.toISOString().slice(11, 19)}</div>
          <div className="uptime">UPTIME {hhmmss(performance.now() / 1000)}</div>
        </div>
      )}
    </header>
  );
}

function hhmmss(totalSeconds: number): string {
  const t = Math.max(0, Math.floor(totalSeconds));
  const h = Math.floor(t / 3600).toString().padStart(2, '0');
  const m = Math.floor((t % 3600) / 60).toString().padStart(2, '0');
  const s = (t % 60).toString().padStart(2, '0');
  return `${h}:${m}:${s}`;
}
