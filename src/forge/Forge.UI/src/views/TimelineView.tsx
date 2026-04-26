import { PlaceholderView } from './PlaceholderView';

export function TimelineView() {
  return (
    <PlaceholderView
      num="02"
      section="Timeline"
      eyebrow={
        <>
          <span className="sep">/</span>
          <span>14-day window</span>
          <span className="sep">/</span>
          <span><span className="live-dot"></span>8 active branches</span>
        </>
      }
      title="Feature & branch flow"
      pendingPhase="Phase 2"
    />
  );
}
