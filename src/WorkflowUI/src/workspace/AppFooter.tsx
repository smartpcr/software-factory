import { useEffect, useState } from 'react';

interface VersionInfoDto {
  version: string;
  commitSha: string;
  buildUtc: string;
}

export function AppFooter() {
  const [versionInfo, setVersionInfo] = useState<VersionInfoDto | null>(null);

  useEffect(() => {
    fetch('/api/version')
      .then((res) => {
        if (!res.ok) throw new Error(`${String(res.status)}`);
        return res.json() as Promise<VersionInfoDto>;
      })
      .then(setVersionInfo)
      .catch(() => setVersionInfo(null));
  }, []);

  const display = versionInfo
    ? `v${versionInfo.version} · ${versionInfo.commitSha}`
    : 'v?';

  const tooltip = versionInfo
    ? `Built: ${versionInfo.buildUtc}`
    : 'Version unavailable';

  return (
    <footer
      className="app-footer"
      style={{
        padding: '4px 12px',
        fontSize: 11,
        color: '#6b7280',
        borderTop: '1px solid #e5e7eb',
        textAlign: 'right',
      }}
      data-testid="app-footer"
      title={tooltip}
    >
      {display}
    </footer>
  );
}
