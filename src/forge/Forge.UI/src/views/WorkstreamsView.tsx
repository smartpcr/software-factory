import { PlaceholderView } from './PlaceholderView';

export function WorkstreamsView() {
  return (
    <PlaceholderView
      num="03"
      section="Workstreams"
      eyebrow={
        <>
          <span className="sep">/</span>
          <span>5 active · 2 merging · 1 planning</span>
          <span className="sep">/</span>
          <span><span className="live-dot"></span>1 conflict</span>
        </>
      }
      title="Parallel workstreams"
      pendingPhase="Phase 3"
    />
  );
}
