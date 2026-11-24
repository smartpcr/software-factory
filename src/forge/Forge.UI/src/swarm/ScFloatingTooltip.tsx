// Themed hover tooltip for swarm-control elements that sit inside clipping
// scrollers (the story delivery strip). The CSS-only [data-sc-tooltip] is a
// pseudo-element, so an overflow:hidden ancestor cuts it off; this one is
// portalled to <body> and positioned against the anchor's viewport rect.

import { useEffect } from 'react';
import { createPortal } from 'react-dom';

export interface ScTip {
  /** Tooltip lines; the first renders as the heading. */
  lines: string[];
  /** Viewport rect of the element the tooltip describes. */
  anchor: DOMRect;
}

const MAX_WIDTH = 520;
const GAP = 8;

export function ScFloatingTooltip({ tip, onDismiss }: { tip: ScTip | null; onDismiss: () => void }) {
  // A fixed-position tooltip would drift from its anchor on scroll; drop it.
  useEffect(() => {
    if (!tip) return undefined;
    window.addEventListener('scroll', onDismiss, true);
    window.addEventListener('resize', onDismiss);
    return () => {
      window.removeEventListener('scroll', onDismiss, true);
      window.removeEventListener('resize', onDismiss);
    };
  }, [tip, onDismiss]);

  if (!tip || tip.lines.length === 0) return null;
  const { anchor } = tip;
  const left = Math.max(GAP, Math.min(anchor.left, window.innerWidth - MAX_WIDTH - GAP));
  // Below the anchor unless that leaves too little room; then above it.
  const below = window.innerHeight - anchor.bottom > 220;
  const style = below
    ? { left, top: anchor.bottom + GAP, maxWidth: MAX_WIDTH }
    : { left, bottom: window.innerHeight - anchor.top + GAP, maxWidth: MAX_WIDTH };
  const [head, ...rest] = tip.lines;
  // The wrapper carries `swarm-ctrl` so the tooltip picks up the scoped
  // light/dark theme tokens outside the page's own subtree.
  return createPortal(
    <div className="swarm-ctrl sc-float-tip-layer">
      <div className="sc-float-tip" role="tooltip" style={style}>
        <div className="sc-float-tip__head">{head}</div>
        {rest.map((line, i) => (
          <div key={i} className="sc-float-tip__line">{line}</div>
        ))}
      </div>
    </div>,
    document.body,
  );
}