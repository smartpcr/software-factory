interface PlaceholderProps {
  num: string;
  section: string;
  eyebrow: React.ReactNode;
  title: React.ReactNode;
  pendingPhase: string;
}

// Every Phase-0 view renders the prototype page-head + an empty-state body.
// Phases 1–4 replace each placeholder with the real view content.
export function PlaceholderView({ num, section, eyebrow, title, pendingPhase }: PlaceholderProps) {
  return (
    <section className="view active">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">{num}</span>&nbsp;{section}</span>
            {eyebrow}
          </div>
          <h1 className="page-title">
            <span className="mark"></span>
            {title}
          </h1>
        </div>
      </div>
      <div className="empty-state">
        <div className="title">{section}</div>
        <div className="sub">Phase 0 scaffolding in place. View content lands in {pendingPhase}.</div>
      </div>
    </section>
  );
}
