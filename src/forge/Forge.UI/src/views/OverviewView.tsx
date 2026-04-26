import { PlaceholderView } from './PlaceholderView';

export function OverviewView() {
  return (
    <PlaceholderView
      num="00"
      section="Overview"
      eyebrow={
        <>
          <span className="sep">/</span>
          <span>last 24h</span>
          <span className="sep">/</span>
          <span><span className="live-dot"></span>6 agents working</span>
        </>
      }
      title="Atlas factory floor"
      pendingPhase="a future phase"
    />
  );
}
