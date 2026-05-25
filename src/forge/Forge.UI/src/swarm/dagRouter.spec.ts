// Pure-logic tests for the workstream DAG arrow router. Covers the
// defensive hardening pass: lane assignment for horiz/horiz-back,
// unsafe-Z fallback into rail mode, and the existing impl→qa-e2e
// vertical contract. No DOM — everything runs against synthetic
// `Anchor` boxes so the visual behavior is locked in independently
// of the SwarmDetailPanel measurement code.

import { describe, expect, it } from 'vitest';
import { routeArrows, type Anchor, type RawArrow } from './dagRouter';

const W = 224;
const H = 96;

function card(x: number, y: number, w = W, h = H): Anchor {
  return { x, y, w, h };
}

function edge(key: string, from: Anchor, to: Anchor): RawArrow {
  return { key, from, to, tone: 'normal' };
}

describe('routeArrows — vertical impl → qa-e2e (Stage 8.2 contract)', () => {
  it('emits a straight 2-point vertical when source and target share x', () => {
    const impl = card(100, 100);
    const e2e = card(100, 100 + H + 8);
    const out = routeArrows({
      edges: [edge('impl->e2e', impl, e2e)],
      obstacles: [impl, e2e],
    });
    expect(out).toHaveLength(1);
    expect(out[0].mode).toBe('vert');
    expect(out[0].points).toHaveLength(2);
    expect(out[0].points[0].x).toBeCloseTo(impl.x + W / 2);
    expect(out[0].points[1].x).toBeCloseTo(impl.x + W / 2);
    expect(out[0].points[1].y - out[0].points[0].y).toBeCloseTo(8);
  });
});

describe('routeArrows — horizontal cross-depth (single edge)', () => {
  it('emits a 4-point Z and falls in the column gap', () => {
    const a = card(0, 100);
    const b = card(W + 56, 100);
    const out = routeArrows({ edges: [edge('a->b', a, b)], obstacles: [a, b] });
    expect(out[0].mode).toBe('horiz');
    expect(out[0].points).toHaveLength(4);
    const midX = out[0].points[1].x;
    expect(midX).toBeGreaterThan(a.x + W);
    expect(midX).toBeLessThan(b.x);
  });
});

describe('routeArrows — P1 lane assignment for horiz edges', () => {
  it('gives two cross-pattern edges sharing a column gap distinct midX values', () => {
    // Two edges crossing through the same column gap whose vertical
    // segments at midX fully overlap — the classic case where one
    // would otherwise draw on top of the other. Cross pattern:
    // A1 (top) → B1 (bottom), A2 (bottom) → B2 (top).
    const a1 = card(0, 100);
    const a2 = card(0, 400);
    const b1 = card(W + 56, 400);
    const b2 = card(W + 56, 100);
    const out = routeArrows({
      edges: [edge('a1->b1', a1, b1), edge('a2->b2', a2, b2)],
      obstacles: [a1, a2, b1, b2],
    });
    expect(out.every((r) => r.mode === 'horiz')).toBe(true);
    const midX1 = out[0].points[1].x;
    const midX2 = out[1].points[1].x;
    expect(midX1).not.toBeCloseTo(midX2);
    // Both midX should land inside the column gap.
    expect(midX1).toBeGreaterThan(a1.x + W);
    expect(midX1).toBeLessThan(b1.x);
    expect(midX2).toBeGreaterThan(a2.x + W);
    expect(midX2).toBeLessThan(b2.x);
  });

  it('keeps midX centered around the natural gap mid when only one edge uses the bucket', () => {
    const a = card(0, 100);
    const b = card(W + 56, 100);
    const out = routeArrows({ edges: [edge('a->b', a, b)], obstacles: [a, b] });
    const natural = (a.x + W + b.x) / 2;
    expect(out[0].points[1].x).toBeCloseTo(natural);
  });

  it('caps lane step so all lanes stay inside the gap with safety padding', () => {
    // Four edges with cross-overlapping spans — all four vertical
    // segments at midX would collide without lane assignment.
    const a1 = card(0, 100);
    const a2 = card(0, 200);
    const a3 = card(0, 300);
    const a4 = card(0, 400);
    const b1 = card(W + 56, 400);
    const b2 = card(W + 56, 300);
    const b3 = card(W + 56, 200);
    const b4 = card(W + 56, 100);
    const out = routeArrows({
      edges: [
        edge('a1->b1', a1, b1),
        edge('a2->b2', a2, b2),
        edge('a3->b3', a3, b3),
        edge('a4->b4', a4, b4),
      ],
      obstacles: [a1, a2, a3, a4, b1, b2, b3, b4],
    });
    for (const r of out) {
      const midX = r.points[1].x;
      // Inside the column gap.
      expect(midX).toBeGreaterThan(a1.x + W);
      expect(midX).toBeLessThan(b1.x);
      expect(r.lane).toBeGreaterThanOrEqual(0);
    }
    const midXs = out.map((r) => r.points[1].x);
    const uniqueMidXs = new Set(midXs.map((v) => Math.round(v * 100)));
    expect(uniqueMidXs.size).toBe(midXs.length);
  });
});

describe('routeArrows — P1 lane assignment for horiz-back edges', () => {
  it('mirrors lane-assignment behavior for R→L snake steps with overlapping spans', () => {
    // Cross pattern in the R→L direction. Sources on the RIGHT,
    // targets on the LEFT, vertical segments at midX fully overlap.
    const s1 = card(W + 56, 100);
    const s2 = card(W + 56, 400);
    const t1 = card(0, 400);
    const t2 = card(0, 100);
    const out = routeArrows({
      edges: [edge('s1->t1', s1, t1), edge('s2->t2', s2, t2)],
      obstacles: [s1, s2, t1, t2],
    });
    expect(out.every((r) => r.mode === 'horiz-back')).toBe(true);
    const midX1 = out[0].points[1].x;
    const midX2 = out[1].points[1].x;
    expect(midX1).not.toBeCloseTo(midX2);
    // Both midX should land inside the gap (between target.right and source.left).
    expect(midX1).toBeGreaterThan(t1.x + W);
    expect(midX1).toBeLessThan(s1.x);
    expect(midX2).toBeGreaterThan(t2.x + W);
    expect(midX2).toBeLessThan(s2.x);
  });
});

describe('routeArrows — unsafe-Z fallback to rail', () => {
  it('downgrades a skip-edge whose midX falls inside an intermediate card', () => {
    // Depths 0, 1, 2 with width-W cards. An edge from depth-0 to
    // depth-2 has midX = (W + ((W + 56) * 2)) / 2 = ~270 for W=224.
    // The depth-1 card lives at x = W + 56 = 280..504 — midX 270 is
    // just outside, so to make midX fall inside the card we use a
    // tighter packing.
    const a = card(0, 100);
    const obstacle = card(W + 20, 100); // sits squarely between a and b
    const b = card(2 * (W + 20), 100);
    const out = routeArrows({
      edges: [edge('a->b', a, b)],
      obstacles: [a, obstacle, b],
    });
    // The vertical leg at midX = mid((a.right, b.left)) sits inside
    // `obstacle` — router must fall back to rail.
    expect(out[0].mode).toBe('rail');
  });

  it('leaves a clean horiz route alone when no obstacles are in the way', () => {
    const a = card(0, 100);
    const b = card(W + 56, 100);
    const farAway = card(0, 1000);
    const out = routeArrows({
      edges: [edge('a->b', a, b)],
      obstacles: [a, farAway, b],
    });
    expect(out[0].mode).toBe('horiz');
  });

  it('downgrades horiz-back when its return path crosses a sibling card', () => {
    const tgt = card(0, 200);
    const obstacle = card(W + 20, 200);
    const src = card(2 * (W + 20), 200);
    const out = routeArrows({
      edges: [edge('src->tgt', src, tgt)],
      obstacles: [tgt, obstacle, src],
    });
    expect(out[0].mode).toBe('rail');
  });
});

describe('routeArrows — rail-lane scheduling preserved', () => {
  it('two overlapping rail edges land on distinct lanes', () => {
    // Force rail by giving edges with no clean horiz/vert classification:
    // target above-left of source (cycle-back arrow).
    const src1 = card(W + 56, 400);
    const tgt1 = card(W + 56, 100); // straight up — vert? dy < 0 disqualifies.
    const src2 = card(W + 56, 500);
    const tgt2 = card(W + 56, 200);
    // pickInitialMode: dx=0, dy<0 → not vert (dy>0 required); dx<w/2 → not horiz;
    // dx>-w/2 → not horiz-back; → 'rail'.
    const out = routeArrows({
      edges: [edge('s1->t1', src1, tgt1), edge('s2->t2', src2, tgt2)],
      obstacles: [src1, tgt1, src2, tgt2],
    });
    expect(out.every((r) => r.mode === 'rail')).toBe(true);
    expect(new Set(out.map((r) => r.lane)).size).toBe(2);
  });
});

describe('routeArrows — d string is well-formed', () => {
  it('always starts with M and contains at least one segment command', () => {
    const a = card(0, 100);
    const b = card(W + 56, 100);
    const out = routeArrows({ edges: [edge('a->b', a, b)], obstacles: [a, b] });
    expect(out[0].d.startsWith('M ')).toBe(true);
    expect(/[LQ]/.test(out[0].d)).toBe(true);
  });
});
