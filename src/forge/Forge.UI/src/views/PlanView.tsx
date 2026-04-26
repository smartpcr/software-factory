import { PlaceholderView } from './PlaceholderView';

export function PlanView() {
  return (
    <PlaceholderView
      num="01"
      section="Plan & Design"
      eyebrow={
        <>
          <span className="sep">/</span>
          <span>day 6 of 10</span>
          <span className="sep">/</span>
          <span><span className="live-dot"></span>6 agents working</span>
        </>
      }
      title={
        <>
          Sprint 14 board
          <span className="sprint-pill">
            <span className="pulse"></span>
            <span>34 / 89 pts</span>
            <span className="sprint-burndown">
              <span className="sprint-burndown-bar"><span className="sprint-burndown-fill" style={{ width: '38%' }}></span></span>
            </span>
          </span>
        </>
      }
      pendingPhase="Phase 1"
    />
  );
}
