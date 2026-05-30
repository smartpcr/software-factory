// -----------------------------------------------------------------------
// Shared collapse-chevron button used by the topbar, spawn header,
// conversation KPI strip, and other chrome regions. Matches the style of
// the sidebar collapse button (28×28 transparent square that gets a
// hover background) with one shared SVG chevron that rotates 180° via
// CSS when the target is collapsed — so expanded and collapsed states
// visually mirror each other instead of swapping unicode glyphs.
// -----------------------------------------------------------------------

import type { CSSProperties } from 'react';

interface Props {
  /**
   * When true, the chevron rotates 180° (target is currently collapsed,
   * so the button means "click to expand"). When false, the chevron sits
   * in its default orientation (target is currently expanded, button
   * means "click to collapse").
   */
  collapsed: boolean;
  /** Tooltip + a11y label. Same text used for both states by default. */
  title: string;
  ariaLabel?: string;
  onClick: () => void;
  className?: string;
  style?: CSSProperties;
  /**
   * Chevron orientation. `'vertical'` (default) means an up-pointing
   * chevron that rotates to down when collapsed — for collapsing chrome
   * upward (topbar, spawn header, KPI strip). `'horizontal'` means a
   * left-pointing chevron that rotates to right when collapsed — for
   * collapsing chrome sideways (sidebar-style).
   */
  orientation?: 'vertical' | 'horizontal';
}

export function ChromeCollapseButton({
  collapsed,
  title,
  ariaLabel,
  onClick,
  className,
  style,
  orientation = 'vertical',
}: Props): JSX.Element {
  const rotatedClass = collapsed ? ' chrome-collapse-btn--rotated' : '';
  const extra = className ? ` ${className}` : '';
  // Vertical chevron points up by default (collapse upward); horizontal
  // points left by default (collapse leftward — matches sidebar arrow).
  const path = orientation === 'horizontal'
    ? 'M9 3 L 5 7 L 9 11'
    : 'M3 9 L 7 5 L 11 9';
  return (
    <button
      type="button"
      className={`chrome-collapse-btn${rotatedClass}${extra}`}
      title={title}
      aria-label={ariaLabel ?? title}
      onClick={onClick}
      style={style}
    >
      <svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden="true">
        <path
          d={path}
          stroke="currentColor"
          strokeWidth="1.4"
          fill="none"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
      </svg>
    </button>
  );
}
