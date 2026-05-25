// docs/forge/e2e.md Stage 8.2 + snake-layout follow-up — pure arrow
// router for the workstream DAG view. Extracted from SwarmDetailPanel
// so the routing modes (rail / horiz / horiz-back / vert), lane
// assignment, and unsafe-Z fallback are unit-testable without a DOM.
//
// Routing modes (in selection-precedence order, first match wins):
//   * 'vert'       — source & target share roughly the same x and
//                    target is below source. Straight bottom→top
//                    vertical (with a mid-Y bend if x drift > 1px).
//                    Covers impl→qa-e2e (8px gap) and any future
//                    same-sub-column wraps.
//   * 'horiz'      — target clearly to the RIGHT. Z-shape through a
//                    midX placed inside the column gap.
//   * 'horiz-back' — target clearly to the LEFT, no horizontal box
//                    overlap. Mirror of 'horiz', exits source.left,
//                    enters target.right.
//   * 'rail'       — fallback. Detours through the left gutter; lane
//                    assignment by interval scheduling on vertical
//                    span so nested rails don't draw on top of each
//                    other.
//
// Lane assignment now also runs for 'horiz' and 'horiz-back': edges
// sharing the same (quantized) column gap get distinct midX offsets,
// centered around the natural gap midpoint, so fan-in/fan-out chains
// no longer stack their vertical legs on top of each other.
//
// Unsafe-Z fallback: any 'horiz' / 'horiz-back' candidate whose Z
// route would visually pass through a non-endpoint card (inflated by
// a small margin) is downgraded to 'rail' BEFORE lane assignment, so
// the rail's own lane bucket also accounts for the displaced edge.

export interface Anchor {
  x: number;
  y: number;
  w: number;
  h: number;
}

export type ArrowTone = 'normal' | 'blocked';

export type RouteMode = 'rail' | 'horiz' | 'horiz-back' | 'vert';

export interface RawArrow {
  key: string;
  from: Anchor;
  to: Anchor;
  tone: ArrowTone;
}

export interface RouterConstants {
  /** Horizontal step (px) between adjacent lanes for horiz/horiz-back. */
  laneStep: number;
  /** Quantization grain (px) for bucketing edges into the same column gap. */
  bucketGrain: number;
  /** Padding (px) inflating obstacle boxes when checking for unsafe-Z crossings. */
  obstaclePadding: number;
  /** Lane-0 rail offset to the left of the leftmost card. */
  railGap: number;
  /** Additional left offset per nested rail lane. */
  railLaneGap: number;
  /** Minimum x for a rail (clamps the rail inside the phase box). */
  railMinX: number;
  /** Rounded-elbow radius for orthogonal joins. */
  cornerR: number;
}

export const DEFAULT_ROUTER_CONSTANTS: RouterConstants = {
  laneStep: 12,
  bucketGrain: 8,
  obstaclePadding: 4,
  railGap: 12,
  railLaneGap: 8,
  railMinX: 3,
  cornerR: 6,
};

export interface RoutedEdge {
  key: string;
  tone: ArrowTone;
  mode: RouteMode;
  lane: number;
  points: Array<{ x: number; y: number }>;
  d: string;
}

export interface RouterInput {
  edges: RawArrow[];
  /** All card boxes in the diagram; per-edge endpoints are excluded automatically. */
  obstacles: Anchor[];
  constants?: Partial<RouterConstants>;
}

/** Build an SVG path through `points` with rounded corners of radius `r`
 *  at each interior vertex. Degenerate corners (segment shorter than the
 *  radius) fall back to a sharp `L` join. */
export function orthogonalPath(
  points: ReadonlyArray<{ x: number; y: number }>,
  r: number,
): string {
  if (points.length === 0) return '';
  if (points.length === 1) return `M ${points[0].x} ${points[0].y}`;
  let d = `M ${points[0].x} ${points[0].y}`;
  for (let i = 1; i < points.length - 1; i++) {
    const p0 = points[i - 1];
    const p1 = points[i];
    const p2 = points[i + 1];
    const len1 = Math.hypot(p1.x - p0.x, p1.y - p0.y) || 1;
    const len2 = Math.hypot(p2.x - p1.x, p2.y - p1.y) || 1;
    const rr = Math.min(r, len1 / 2, len2 / 2);
    if (rr <= 0.5) { d += ` L ${p1.x} ${p1.y}`; continue; }
    const ax = p1.x - ((p1.x - p0.x) / len1) * rr;
    const ay = p1.y - ((p1.y - p0.y) / len1) * rr;
    const bx = p1.x + ((p2.x - p1.x) / len2) * rr;
    const by = p1.y + ((p2.y - p1.y) / len2) * rr;
    d += ` L ${ax} ${ay} Q ${p1.x} ${p1.y} ${bx} ${by}`;
  }
  const last = points[points.length - 1];
  d += ` L ${last.x} ${last.y}`;
  return d;
}

function quantize(v: number, grain: number): number {
  return Math.round(v / grain) * grain;
}

/** Does the horizontal segment [a..b] at y=Y intersect the inflated card box? */
function horizSegHitsCard(
  segXa: number,
  segXb: number,
  segY: number,
  card: Anchor,
  pad: number,
): boolean {
  const cx1 = card.x - pad;
  const cx2 = card.x + card.w + pad;
  const cy1 = card.y - pad;
  const cy2 = card.y + card.h + pad;
  if (segY < cy1 || segY > cy2) return false;
  const lo = Math.min(segXa, segXb);
  const hi = Math.max(segXa, segXb);
  return hi > cx1 && lo < cx2;
}

/** Does the vertical segment [a..b] at x=X intersect the inflated card box? */
function vertSegHitsCard(
  segYa: number,
  segYb: number,
  segX: number,
  card: Anchor,
  pad: number,
): boolean {
  const cx1 = card.x - pad;
  const cx2 = card.x + card.w + pad;
  const cy1 = card.y - pad;
  const cy2 = card.y + card.h + pad;
  if (segX < cx1 || segX > cx2) return false;
  const lo = Math.min(segYa, segYb);
  const hi = Math.max(segYa, segYb);
  return hi > cy1 && lo < cy2;
}

/** Initial mode selection based on the relative position of the
 *  source/target boxes. Identical to the previous inline classifier
 *  in SwarmDetailPanel so the visual behavior is preserved. */
function pickInitialMode(from: Anchor, to: Anchor): RouteMode {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  if (Math.abs(dx) < from.w / 3 && dy > 0) return 'vert';
  if (dx > from.w / 2) return 'horiz';
  if (dx < -from.w / 2 && to.x + to.w < from.x) return 'horiz-back';
  return 'rail';
}

/** Would a 'horiz' Z-route from `from.right` through `midX` to `to.left`
 *  cross any non-endpoint card box (inflated by `pad`)? */
function horizUnsafe(
  from: Anchor,
  to: Anchor,
  midX: number,
  obstacles: Anchor[],
  pad: number,
): boolean {
  const fromY = from.y + from.h / 2;
  const toY = to.y + to.h / 2;
  const x1 = from.x + from.w;
  const x2 = to.x;
  for (const o of obstacles) {
    if (o === from || o === to) continue;
    if (horizSegHitsCard(x1, midX, fromY, o, pad)) return true;
    if (vertSegHitsCard(fromY, toY, midX, o, pad)) return true;
    if (horizSegHitsCard(midX, x2, toY, o, pad)) return true;
  }
  return false;
}

/** Mirror of `horizUnsafe` for the 'horiz-back' (R→L) Z-route. */
function horizBackUnsafe(
  from: Anchor,
  to: Anchor,
  midX: number,
  obstacles: Anchor[],
  pad: number,
): boolean {
  const fromY = from.y + from.h / 2;
  const toY = to.y + to.h / 2;
  const x1 = from.x;
  const x2 = to.x + to.w;
  for (const o of obstacles) {
    if (o === from || o === to) continue;
    if (horizSegHitsCard(x1, midX, fromY, o, pad)) return true;
    if (vertSegHitsCard(fromY, toY, midX, o, pad)) return true;
    if (horizSegHitsCard(midX, x2, toY, o, pad)) return true;
  }
  return false;
}

interface PendingEdge {
  raw: RawArrow;
  mode: RouteMode;
  midX: number;
  /** Vertical span (top, bottom) — used for lane interval scheduling. */
  span: [number, number];
}

/** Greedy interval scheduling: assign each edge to the lowest lane number
 *  whose previous occupant's `bottom` ≤ this edge's `top`. Returns a map
 *  from edge index → lane number, and the total lane count used. */
function assignLanesGreedy(
  edges: PendingEdge[],
): { laneOf: Map<PendingEdge, number>; laneCount: number } {
  const sorted = [...edges].sort((a, b) => a.span[0] - b.span[0]);
  const laneEnds: number[] = [];
  const laneOf = new Map<PendingEdge, number>();
  for (const e of sorted) {
    const [top, bottom] = e.span;
    let lane = laneEnds.findIndex((end) => end <= top);
    if (lane === -1) {
      lane = laneEnds.length;
      laneEnds.push(bottom);
    } else {
      laneEnds[lane] = bottom;
    }
    laneOf.set(e, lane);
  }
  return { laneOf, laneCount: laneEnds.length };
}

/** Core public entry point. Returns one `RoutedEdge` per input edge,
 *  preserving the input order. */
export function routeArrows(input: RouterInput): RoutedEdge[] {
  const c: RouterConstants = { ...DEFAULT_ROUTER_CONSTANTS, ...(input.constants ?? {}) };

  // 1) Initial mode + natural midX per edge.
  const pendings: PendingEdge[] = input.edges.map((e) => {
    const mode = pickInitialMode(e.from, e.to);
    let midX: number;
    if (mode === 'horiz') midX = (e.from.x + e.from.w + e.to.x) / 2;
    else if (mode === 'horiz-back') midX = (e.from.x + e.to.x + e.to.w) / 2;
    else midX = 0;
    const a = e.from.y + e.from.h / 2;
    const b = e.to.y + e.to.h / 2;
    const span: [number, number] = a <= b ? [a, b] : [b, a];
    return { raw: e, mode, midX, span };
  });

  // 2) Unsafe-Z fallback. A horiz/horiz-back whose Z would cross a
  //    non-endpoint card gets downgraded to 'rail' BEFORE bucket
  //    assignment so the displaced edge competes for rail lanes too.
  for (const p of pendings) {
    if (p.mode === 'horiz' && horizUnsafe(p.raw.from, p.raw.to, p.midX, input.obstacles, c.obstaclePadding)) {
      p.mode = 'rail';
    } else if (p.mode === 'horiz-back' && horizBackUnsafe(p.raw.from, p.raw.to, p.midX, input.obstacles, c.obstaclePadding)) {
      p.mode = 'rail';
    }
  }

  // 3) Bucket horiz/horiz-back by quantized column gap; assign lanes
  //    per bucket and center the lane offsets around the natural midX.
  //    Quantization absorbs sub-pixel DOM drift so coincident gaps
  //    bucket together even when measurements aren't bit-identical.
  type Bucket = { edges: PendingEdge[]; gapWidth: number };
  const horizBuckets = new Map<string, Bucket>();
  const horizBackBuckets = new Map<string, Bucket>();
  for (const p of pendings) {
    if (p.mode === 'horiz') {
      const srcRight = p.raw.from.x + p.raw.from.w;
      const tgtLeft = p.raw.to.x;
      const key = `h:${quantize(srcRight, c.bucketGrain)}:${quantize(tgtLeft, c.bucketGrain)}`;
      const b = horizBuckets.get(key) ?? { edges: [], gapWidth: Math.max(0, tgtLeft - srcRight) };
      b.edges.push(p);
      horizBuckets.set(key, b);
    } else if (p.mode === 'horiz-back') {
      const srcLeft = p.raw.from.x;
      const tgtRight = p.raw.to.x + p.raw.to.w;
      const key = `hb:${quantize(srcLeft, c.bucketGrain)}:${quantize(tgtRight, c.bucketGrain)}`;
      const b = horizBackBuckets.get(key) ?? { edges: [], gapWidth: Math.max(0, srcLeft - tgtRight) };
      b.edges.push(p);
      horizBackBuckets.set(key, b);
    }
  }
  const laneOfEdge = new Map<PendingEdge, number>();
  const laneCountOfEdge = new Map<PendingEdge, number>();
  const stepOfEdge = new Map<PendingEdge, number>();
  for (const b of [...horizBuckets.values(), ...horizBackBuckets.values()]) {
    const { laneOf, laneCount } = assignLanesGreedy(b.edges);
    // Cap step so all lanes stay inside the gap with safety padding on
    // each side. With safety = obstaclePadding the centered band uses
    // at most (gapWidth - 2*safety) px.
    const safety = c.obstaclePadding;
    const usable = Math.max(0, b.gapWidth - 2 * safety);
    const maxStep = laneCount > 1 ? usable / (laneCount - 1) : c.laneStep;
    const step = Math.min(c.laneStep, Math.max(0, maxStep));
    for (const e of b.edges) {
      laneOfEdge.set(e, laneOf.get(e) ?? 0);
      laneCountOfEdge.set(e, laneCount);
      stepOfEdge.set(e, step);
    }
  }

  // 4) Rail-lane assignment over the (possibly enlarged) set of rail
  //    edges — same greedy interval scheduling as before but factored
  //    into the shared helper.
  const railEdges = pendings.filter((p) => p.mode === 'rail');
  const { laneOf: railLaneOf } = assignLanesGreedy(railEdges);
  for (const r of railEdges) laneOfEdge.set(r, railLaneOf.get(r) ?? 0);

  // 5) Emit points + d per edge.
  const out: RoutedEdge[] = [];
  for (const p of pendings) {
    const fromY = p.raw.from.y + p.raw.from.h / 2;
    const toY = p.raw.to.y + p.raw.to.h / 2;
    let pts: Array<{ x: number; y: number }>;
    const lane = laneOfEdge.get(p) ?? 0;
    if (p.mode === 'horiz') {
      const count = laneCountOfEdge.get(p) ?? 1;
      const step = stepOfEdge.get(p) ?? 0;
      const offset = (lane - (count - 1) / 2) * step;
      const midX = p.midX + offset;
      const x1 = p.raw.from.x + p.raw.from.w;
      const x2 = p.raw.to.x;
      pts = [
        { x: x1, y: fromY },
        { x: midX, y: fromY },
        { x: midX, y: toY },
        { x: x2, y: toY },
      ];
    } else if (p.mode === 'horiz-back') {
      const count = laneCountOfEdge.get(p) ?? 1;
      const step = stepOfEdge.get(p) ?? 0;
      const offset = (lane - (count - 1) / 2) * step;
      const midX = p.midX + offset;
      const x1 = p.raw.from.x;
      const x2 = p.raw.to.x + p.raw.to.w;
      pts = [
        { x: x1, y: fromY },
        { x: midX, y: fromY },
        { x: midX, y: toY },
        { x: x2, y: toY },
      ];
    } else if (p.mode === 'vert') {
      const startY = p.raw.from.y + p.raw.from.h;
      const endY = p.raw.to.y;
      const fromCx = p.raw.from.x + p.raw.from.w / 2;
      const toCx = p.raw.to.x + p.raw.to.w / 2;
      if (Math.abs(fromCx - toCx) < 1) {
        pts = [
          { x: fromCx, y: startY },
          { x: fromCx, y: endY },
        ];
      } else {
        const midY = (startY + endY) / 2;
        pts = [
          { x: fromCx, y: startY },
          { x: fromCx, y: midY },
          { x: toCx, y: midY },
          { x: toCx, y: endY },
        ];
      }
    } else {
      const cardLeft = Math.min(p.raw.from.x, p.raw.to.x);
      const railX = Math.max(c.railMinX, cardLeft - c.railGap - lane * c.railLaneGap);
      pts = [
        { x: p.raw.from.x, y: fromY },
        { x: railX, y: fromY },
        { x: railX, y: toY },
        { x: p.raw.to.x, y: toY },
      ];
    }
    out.push({
      key: p.raw.key,
      tone: p.raw.tone,
      mode: p.mode,
      lane,
      points: pts,
      d: orthogonalPath(pts, c.cornerR),
    });
  }
  return out;
}
