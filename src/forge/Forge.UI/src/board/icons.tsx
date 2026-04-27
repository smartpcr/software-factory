// Verbatim port of the prototype's `icons` map (lines 3283-3291 of forge-board.html).
// Inlined as JSX so React renders identical SVG markup; do not swap to an icon library.

import type { CardType } from '@/types';

export function TypeIcon({ type }: { type: CardType }) {
  switch (type) {
    case 'story':
      return (
        <svg viewBox="0 0 12 12" fill="none">
          <path d="M2 2 L 9 2 L 9 10 L 6 8 L 3 10 Z" stroke="currentColor" strokeWidth="1.3" fill="none" strokeLinejoin="round" />
        </svg>
      );
    case 'bug':
      return (
        <svg viewBox="0 0 12 12" fill="none">
          <circle cx="6" cy="6.5" r="3" stroke="currentColor" strokeWidth="1.3" />
          <line x1="6" y1="3.5" x2="6" y2="2" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
          <line x1="2.5" y1="6.5" x2="1" y2="6.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
          <line x1="11" y1="6.5" x2="9.5" y2="6.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
        </svg>
      );
    case 'task':
    default:
      return (
        <svg viewBox="0 0 12 12" fill="none">
          <rect x="2" y="2" width="8" height="8" rx="1" stroke="currentColor" strokeWidth="1.3" />
          <path d="M4 6 L 5.5 7.5 L 8 4.5" stroke="currentColor" strokeWidth="1.3" fill="none" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      );
  }
}

export function BoltIcon() {
  return (
    <svg viewBox="0 0 12 12" fill="none">
      <path d="M7 1 L 3 6 H 6 L 5 11 L 9 6 H 6 L 7 1" stroke="currentColor" strokeWidth="1.2" fill="none" strokeLinejoin="round" />
    </svg>
  );
}

export function ShieldIcon() {
  return (
    <svg viewBox="0 0 12 12" fill="none">
      <path d="M6 1.5 L 10 3 V 6.5 C 10 8.5 8.5 10 6 10.5 C 3.5 10 2 8.5 2 6.5 V 3 Z" stroke="currentColor" strokeWidth="1.3" fill="none" />
    </svg>
  );
}

export function BlockIcon() {
  return (
    <svg viewBox="0 0 12 12" fill="none">
      <circle cx="6" cy="6" r="4.5" stroke="currentColor" strokeWidth="1.3" />
      <line x1="3" y1="3" x2="9" y2="9" stroke="currentColor" strokeWidth="1.3" />
    </svg>
  );
}

export function CardOpenIcon() {
  return (
    <svg width="11" height="11" viewBox="0 0 11 11" fill="none">
      <path d="M4 2 H 2 V 9 H 9 V 7 M 6 2 H 9 V 5 M 9 2 L 5 6" stroke="currentColor" strokeWidth="1.2" fill="none" strokeLinecap="round" />
    </svg>
  );
}

export function CardMoreIcon() {
  return (
    <svg width="11" height="11" viewBox="0 0 11 11" fill="none">
      <circle cx="2.5" cy="5.5" r="0.9" fill="currentColor" />
      <circle cx="5.5" cy="5.5" r="0.9" fill="currentColor" />
      <circle cx="8.5" cy="5.5" r="0.9" fill="currentColor" />
    </svg>
  );
}
