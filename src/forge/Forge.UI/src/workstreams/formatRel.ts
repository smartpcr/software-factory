// Verbatim port of the prototype's formatRel (lines 3925-3931).
export function formatRel(d: number): string {
  if (d > -0.01) return 'just now';
  const hours = Math.abs(d) * 24;
  if (hours < 1) return `${Math.round(hours * 60)}m ago`;
  if (hours < 24) return `${Math.round(hours)}h ago`;
  return `${Math.round(hours / 24)}d ago`;
}
