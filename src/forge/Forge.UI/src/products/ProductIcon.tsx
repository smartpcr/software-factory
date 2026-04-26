import type { Product } from '@/types';

interface Props {
  product: Pick<Product, 'id' | 'name' | 'slug'>;
  size?: number;
  /**
   * When true, the icon renders without rounding so it can sit inside another
   * rounded container (e.g. a select trigger). Default = rounded.
   */
  square?: boolean;
}

// Deterministic, decorative product icon — gradient background + monogram +
// a small offset accent dot (so two products with the same initials still
// look different). All inputs are derived from the product id/slug so the
// same product looks identical on every reload.
export function ProductIcon({ product, size = 48, square = false }: Props) {
  const seed = `${product.id || product.slug || product.name}`;
  const palette = pickPalette(seed);
  const initials = getInitials(product.name);
  const dotPos = pickDotCorner(seed);
  const gradId = `pi-grad-${hashString(seed)}`;
  const fontSize = Math.round(size * 0.42);
  const dotR = Math.max(2, Math.round(size * 0.085));
  const dotInset = Math.round(size * 0.16);

  return (
    <span
      role="img"
      aria-label={`${product.name} icon`}
      style={{
        display: 'inline-flex',
        width: size,
        height: size,
        borderRadius: square ? 0 : Math.round(size * 0.22),
        overflow: 'hidden',
        flexShrink: 0,
        boxShadow: '0 1px 2px rgba(0,0,0,0.10), inset 0 0 0 1px rgba(255,255,255,0.10)',
      }}
    >
      <svg
        width={size}
        height={size}
        viewBox={`0 0 ${size} ${size}`}
        xmlns="http://www.w3.org/2000/svg"
        aria-hidden="true"
      >
        <defs>
          <linearGradient id={gradId} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0%" stopColor={palette.from} />
            <stop offset="100%" stopColor={palette.to} />
          </linearGradient>
        </defs>

        {/* gradient background */}
        <rect width={size} height={size} fill={`url(#${gradId})`} />

        {/* monogram */}
        <text
          x="50%"
          y="50%"
          dominantBaseline="central"
          textAnchor="middle"
          fill="rgba(255,255,255,0.96)"
          style={{
            fontFamily: 'var(--font-display, "Inter", system-ui, sans-serif)',
            fontSize,
            fontWeight: 700,
            letterSpacing: -0.4,
          }}
        >
          {initials}
        </text>

        {/* accent dot — corner picked from hash */}
        <circle
          cx={dotPos.x === 'l' ? dotInset : size - dotInset}
          cy={dotPos.y === 't' ? dotInset : size - dotInset}
          r={dotR}
          fill={palette.dot}
          opacity={0.92}
        />
      </svg>
    </span>
  );
}

// Render the same SVG as a standalone string — useful for the favicon
// and for any place the React component can't be mounted (e.g. document.head).
export function renderProductIconSvg(
  product: Pick<Product, 'id' | 'name' | 'slug'>,
  size = 64,
): string {
  const seed = `${product.id || product.slug || product.name}`;
  const palette = pickPalette(seed);
  const initials = getInitials(product.name);
  const dotPos = pickDotCorner(seed);
  const gradId = `g${hashString(seed)}`;
  const fontSize = Math.round(size * 0.42);
  const dotR = Math.max(2, Math.round(size * 0.085));
  const dotInset = Math.round(size * 0.16);
  const dotCx = dotPos.x === 'l' ? dotInset : size - dotInset;
  const dotCy = dotPos.y === 't' ? dotInset : size - dotInset;
  const radius = Math.round(size * 0.22);

  return [
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${size} ${size}" width="${size}" height="${size}">`,
    '<defs>',
    `<linearGradient id="${gradId}" x1="0" y1="0" x2="1" y2="1">`,
    `<stop offset="0%" stop-color="${palette.from}"/>`,
    `<stop offset="100%" stop-color="${palette.to}"/>`,
    '</linearGradient>',
    `<clipPath id="c${hashString(seed)}"><rect width="${size}" height="${size}" rx="${radius}" ry="${radius}"/></clipPath>`,
    '</defs>',
    `<g clip-path="url(#c${hashString(seed)})">`,
    `<rect width="${size}" height="${size}" fill="url(#${gradId})"/>`,
    `<text x="50%" y="50%" dominant-baseline="central" text-anchor="middle" `,
    `fill="rgba(255,255,255,0.96)" font-family="Inter, system-ui, sans-serif" `,
    `font-size="${fontSize}" font-weight="700" letter-spacing="-0.4">${escapeXml(initials)}</text>`,
    `<circle cx="${dotCx}" cy="${dotCy}" r="${dotR}" fill="${palette.dot}" opacity="0.92"/>`,
    '</g>',
    '</svg>',
  ].join('');
}

/**
 * Returns a data URL for the product's SVG icon, suitable for `<link rel="icon">`
 * or any other context that wants a single-string image source.
 */
export function productIconDataUrl(
  product: Pick<Product, 'id' | 'name' | 'slug'>,
  size = 64,
): string {
  const svg = renderProductIconSvg(product, size);
  return `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`;
}

function escapeXml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

// --- helpers ------------------------------------------------------------

function getInitials(name: string): string {
  const cleaned = (name || '').trim();
  if (!cleaned) return '·';
  const words = cleaned.split(/[\s_\-./]+/).filter(Boolean);
  if (words.length === 0) return cleaned.slice(0, 2).toUpperCase();
  if (words.length === 1) {
    const w = words[0];
    return w.length >= 2 ? (w[0] + w[1]).toUpperCase() : w[0].toUpperCase();
  }
  return (words[0][0] + words[1][0]).toUpperCase();
}

// Stable 32-bit hash. Good enough for picking palette / corner.
function hashString(s: string): number {
  let h = 2166136261 >>> 0;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 16777619) >>> 0;
  }
  return h;
}

interface Palette {
  from: string;
  to: string;
  dot: string;
}

// Hand-picked, accessible-on-white palettes that look good as gradients.
// Each entry is (start, end, accent-dot). Order matters for variety — the
// hash picks an index so the same product always gets the same palette.
const PALETTES: Palette[] = [
  { from: '#5B6CFF', to: '#9D7BFF', dot: '#FFFFFF' }, // indigo → violet
  { from: '#10B981', to: '#0EA5E9', dot: '#FFFFFF' }, // emerald → sky
  { from: '#F59E0B', to: '#EF4444', dot: '#FFFFFF' }, // amber → red
  { from: '#EC4899', to: '#8B5CF6', dot: '#FFFFFF' }, // pink → violet
  { from: '#06B6D4', to: '#3B82F6', dot: '#FFFFFF' }, // cyan → blue
  { from: '#84CC16', to: '#22C55E', dot: '#FFFFFF' }, // lime → green
  { from: '#F97316', to: '#FB7185', dot: '#FFFFFF' }, // orange → rose
  { from: '#6366F1', to: '#22D3EE', dot: '#FFFFFF' }, // indigo → cyan
  { from: '#A855F7', to: '#EC4899', dot: '#FFFFFF' }, // purple → pink
  { from: '#0F766E', to: '#65A30D', dot: '#FFFFFF' }, // teal → lime
  { from: '#7C3AED', to: '#2563EB', dot: '#FFFFFF' }, // violet → blue
  { from: '#DC2626', to: '#F59E0B', dot: '#FFFFFF' }, // red → amber
];

function pickPalette(seed: string): Palette {
  return PALETTES[hashString(seed) % PALETTES.length];
}

function pickDotCorner(seed: string): { x: 'l' | 'r'; y: 't' | 'b' } {
  // Use a different mix bit so corner doesn't perfectly correlate with palette.
  const h = hashString(seed + '#corner');
  return {
    x: (h & 1) === 0 ? 'l' : 'r',
    y: (h & 2) === 0 ? 't' : 'b',
  };
}
