import { useNavigate } from 'react-router-dom';

export interface CrumbSpec {
  /** small all-caps prefix shown above the label, e.g. "MODE" */
  pre?: string;
  label: string;
  href?: string;
  active?: boolean;
}

interface Props {
  crumbs: CrumbSpec[];
  /** show a back button on the right */
  onBack?: () => void;
  /** show "OVERVIEW" home button on the right */
  homeHref?: string;
}

export function SwarmNavBar({ crumbs, onBack, homeHref = '/workstreams' }: Props) {
  const navigate = useNavigate();

  return (
    <nav className="nav-bar">
      <div className="crumbs">
        {crumbs.map((c, i) => (
          <span key={i} style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
            {i > 0 && <span className="crumb-sep">/</span>}
            <a
              className={`crumb${c.active ? ' active' : ''}`}
              onClick={(e) => {
                e.preventDefault();
                if (c.href) navigate(c.href);
              }}
              href={c.href}
            >
              {c.pre && <span className="pre">{c.pre}</span>}
              <span>{c.label}</span>
            </a>
          </span>
        ))}
      </div>
      <div className="nav-ops">
        {onBack && <button className="nav-btn" onClick={onBack}>◂ BACK</button>}
        <button className="nav-btn go" onClick={() => navigate(homeHref)}>⌂ OVERVIEW</button>
      </div>
    </nav>
  );
}
