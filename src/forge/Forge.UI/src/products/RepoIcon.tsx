import type { RepoKind } from '@/types';

// Phase 8.7+ — small inline-SVG glyph that indicates the repo's provider.
// ADO uses Azure DevOps' angled-square mark; GitHub uses the canonical
// Octocat silhouette. Sized to align with the existing "dot" baseline so
// it slots into repo lists without bumping the row height.
interface Props {
  kind: RepoKind;
  size?: number;
  title?: string;
}

const ADO_BLUE = '#0078D4';
const GH_INK = 'currentColor';

export function RepoIcon({ kind, size = 16, title }: Props) {
  const tooltip = title ?? (kind === 'ado' ? 'Azure DevOps' : 'GitHub');
  return kind === 'ado' ? (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      role="img"
      aria-label={tooltip}
    >
      <title>{tooltip}</title>
      {/* Azure DevOps cube mark — three faces in the brand blue. */}
      <path
        fill={ADO_BLUE}
        d="M0 8.877L2.247 5.91l8.405-3.416V.022l7.37 5.393L2.966 8.338v9.349L0 16.707zm24-4.45v14.974l-5.72 4.71-9.255-3.046v3.022l-6.058-7.451 15.18 1.193V5.281z"
      />
    </svg>
  ) : (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      role="img"
      aria-label={tooltip}
      fill={GH_INK}
    >
      <title>{tooltip}</title>
      {/* GitHub Octocat silhouette — the canonical mark, single path. */}
      <path d="M12 .5C5.65.5.5 5.65.5 12c0 5.08 3.29 9.39 7.86 10.91.58.11.79-.25.79-.56v-2c-3.2.7-3.87-1.37-3.87-1.37-.52-1.32-1.27-1.67-1.27-1.67-1.04-.71.08-.7.08-.7 1.15.08 1.76 1.18 1.76 1.18 1.02 1.75 2.68 1.24 3.34.95.1-.74.4-1.24.73-1.53-2.55-.29-5.24-1.28-5.24-5.7 0-1.26.45-2.29 1.18-3.1-.12-.29-.51-1.46.11-3.04 0 0 .97-.31 3.18 1.18a11.05 11.05 0 0 1 5.79 0c2.21-1.49 3.18-1.18 3.18-1.18.62 1.58.23 2.75.11 3.04.74.81 1.18 1.84 1.18 3.1 0 4.43-2.69 5.41-5.25 5.69.41.36.78 1.06.78 2.15v3.18c0 .31.21.68.8.56A11.5 11.5 0 0 0 23.5 12C23.5 5.65 18.35.5 12 .5z" />
    </svg>
  );
}
