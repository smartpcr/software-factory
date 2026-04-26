import React from 'react';
import { correlationId, logAction, recentActions } from '@/telemetry';

// Phase 7.4 — view-level error boundary. Catches render errors so a single
// broken view doesn't blank the whole app, and emits a "report problem"
// payload (last-50 actions + correlation id + message + stack) the user can
// paste into a bug report.

interface Props { name: string; children: React.ReactNode; }
interface State { error: Error | null; corr: string | null; }

export class ErrorBoundary extends React.Component<Props, State> {
  constructor(props: Props) {
    super(props);
    this.state = { error: null, corr: null };
  }

  static getDerivedStateFromError(error: Error): State {
    return { error, corr: correlationId() };
  }

  componentDidCatch(error: Error, info: React.ErrorInfo): void {
    logAction('forge.error', { view: this.props.name, message: error.message, stack: info.componentStack });
    // eslint-disable-next-line no-console
    console.error(`[forge:${this.props.name}]`, error, info);
  }

  reset = () => this.setState({ error: null, corr: null });

  copyReport = async () => {
    const payload = {
      correlation: this.state.corr,
      view: this.props.name,
      message: this.state.error?.message,
      stack: this.state.error?.stack,
      url: window.location.href,
      ua: navigator.userAgent,
      actions: recentActions(),
      at: new Date().toISOString(),
    };
    const text = JSON.stringify(payload, null, 2);
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      // Clipboard might not be available (insecure context) — fall back to log.
      // eslint-disable-next-line no-console
      console.log('[forge:report]', text);
    }
  };

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <div className="empty-state" role="alert">
        <div className="title">Something broke in {this.props.name}</div>
        <div className="sub">
          {this.state.error.message}
          <br />
          <span style={{ fontFamily: 'var(--font-mono)', fontSize: 10 }}>
            correlation: {this.state.corr}
          </span>
        </div>
        <div className="actions">
          <button className="btn btn-primary" onClick={this.reset}>Try again</button>
          <button className="btn btn-secondary" onClick={() => void this.copyReport()}>Copy report</button>
        </div>
      </div>
    );
  }
}
