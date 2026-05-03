export type SwarmMode = 'design' | 'impl' | 'release';
type StageState = 'done' | 'active' | 'queued';

export interface PipelineStageSpec {
  mode: SwarmMode;
  num: string;
  label: string;
  detail: string;
  state: StageState;
  badges: { text: string; tone?: 'warn' | 'stl' }[];
}

interface Props {
  stages: PipelineStageSpec[];
  active: SwarmMode | null;
  onSelect: (mode: SwarmMode) => void;
}

export function SwarmPipelineStrip({ stages, active, onSelect }: Props) {
  return (
    <div className="pipeline">
      {stages.map((s) => {
        const cls = ['stage', s.state, active === s.mode ? 'selected' : ''].filter(Boolean).join(' ');
        return (
          <div className={cls} key={s.mode} onClick={() => onSelect(s.mode)}>
            <div className="num">{s.num}</div>
            <div className="meta">
              <div className="lbl">{s.label}</div>
              <div className="val">{s.detail}</div>
              {s.badges.length > 0 && (
                <div className="stage-badges">
                  {s.badges.map((b, i) => (
                    <span key={i} className={`sb-pill${b.tone ? ' ' + b.tone : ''}`}>{b.text}</span>
                  ))}
                </div>
              )}
            </div>
            <div className="dot" />
          </div>
        );
      })}
    </div>
  );
}
