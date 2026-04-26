import { PlaceholderView } from './PlaceholderView';

export function PrsView() {
  return (
    <PlaceholderView
      num="04"
      section="Pull Requests"
      eyebrow={
        <>
          <span className="sep">/</span>
          <span>9 open · 2 need approval</span>
          <span className="sep">/</span>
          <span><span className="live-dot"></span>3 builds running</span>
        </>
      }
      title="Pull request flow"
      pendingPhase="Phase 4"
    />
  );
}
