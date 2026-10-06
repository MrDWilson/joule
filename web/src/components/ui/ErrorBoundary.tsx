import { Component, type ErrorInfo, type ReactNode } from "react";
import { AlertTriangle, Copy, RefreshCw } from "lucide-react";
import { Button } from "./Button";

/**
 * Catches a rendering error so one malformed record can't blank the whole app. The fallback says what happened in
 * plain words, offers Try again (re-render) and Copy details (for a bug report). `resetKey` clears the error when it
 * changes, e.g. on navigation.
 */
export class ErrorBoundary extends Component<
  { children: ReactNode; label?: string; resetKey?: unknown; root?: boolean },
  { error: Error | null; copied: boolean }
> {
  state = { error: null as Error | null, copied: false };

  static getDerivedStateFromError(error: Error) {
    return { error, copied: false };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.warn(`Joule: ${this.props.label ?? "a section"} couldn't be shown.`, error, info.componentStack);
  }

  componentDidUpdate(previous: { resetKey?: unknown }) {
    if (this.state.error && previous.resetKey !== this.props.resetKey) this.setState({ error: null, copied: false });
  }

  private copy = async () => {
    const e = this.state.error;
    if (!e) return;
    const details = [
      `Joule: ${this.props.label ?? "section"} failed to render`,
      `Page: ${window.location.hash || "#/today"}`,
      `Time: ${new Date().toISOString()}`,
      `Browser: ${navigator.userAgent}`,
      `${e.name}: ${e.message}`,
      e.stack ?? "",
    ].join("\n");
    try {
      await navigator.clipboard.writeText(details);
      this.setState({ copied: true });
    } catch {
      window.prompt("Copy these details:", details);
    }
  };

  render() {
    if (!this.state.error) return this.props.children;
    const what = this.props.label ? `${this.props.label} couldn't be shown` : "This section couldn't be shown";
    return (
      <div role="alert" className={`error-notice boundary${this.props.root ? " boundary-root" : ""}`}>
        <AlertTriangle size={18} aria-hidden="true" className="error-notice-icon" />
        <div className="error-notice-copy">
          <strong>{what}.</strong>
          <span>Something in the data was unexpected. The rest of Joule still works.</span>
        </div>
        <div className="button-group">
          <Button variant="secondary" size="sm" onClick={() => this.setState({ error: null, copied: false })}>
            <RefreshCw size={14} aria-hidden="true" />
            Try again
          </Button>
          <Button variant="ghost" size="sm" onClick={() => void this.copy()}>
            <Copy size={14} aria-hidden="true" />
            {this.state.copied ? "Copied" : "Copy details"}
          </Button>
        </div>
      </div>
    );
  }
}
