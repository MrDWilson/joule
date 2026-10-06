import { useCallback, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { AlertCircle, Check, X } from "lucide-react";

/** A next step offered with a toast: "Undo", "View". Pressing it runs it and closes the toast. */
export interface ToastAction {
  label: string;
  onClick: () => void;
}

export interface Toast {
  id: number;
  kind: "success" | "error";
  message: string;
  action?: ToastAction;
}

const LIFETIME = 10_000;

/**
 * One toast stack for the results of your own actions. A success closes after 10s (paused while hovered or focused);
 * an error stays until you dismiss it, so a rejected change is never missed.
 */
export function useToasts() {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const next = useRef(1);
  const push = useCallback((kind: Toast["kind"], message: string, action?: ToastAction) => {
    setToasts((list) => {
      // The same message twice in a row replaces the first rather than stacking up.
      const rest = list.filter((t) => t.message !== message);
      // Only the latest success is shown, and a new error supersedes it, so messages never contradict each other.
      const kept = rest.filter((t) => t.kind === "error");
      return [...kept, { id: next.current++, kind, message, ...(action ? { action } : {}) }].slice(-3);
    });
  }, []);
  const dismiss = useCallback((id: number) => setToasts((list) => list.filter((t) => t.id !== id)), []);
  const clear = useCallback(
    (kind?: Toast["kind"]) => setToasts((list) => (kind ? list.filter((t) => t.kind !== kind) : [])),
    [],
  );
  return { toasts, push, dismiss, clear };
}

function ToastView({ toast, onDismiss }: { toast: Toast; onDismiss: () => void }) {
  const [hovered, setHovered] = useState(false);
  const remaining = useRef(LIFETIME);
  const started = useRef(Date.now());
  const dismissRef = useRef(onDismiss);
  dismissRef.current = onDismiss;
  useEffect(() => {
    if (hovered || toast.kind === "error") return;
    started.current = Date.now();
    const timer = setTimeout(() => dismissRef.current(), remaining.current);
    return () => {
      clearTimeout(timer);
      // Leaving after a hover gives at least a moment to finish reading.
      remaining.current = Math.max(2000, remaining.current - (Date.now() - started.current));
    };
  }, [hovered, toast.kind]);
  const error = toast.kind === "error";
  return (
    <div
      className={`toast${error ? " error-toast" : ""}`}
      role={error ? "alert" : "status"}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      onFocus={() => setHovered(true)}
      onBlur={() => setHovered(false)}
    >
      {error ? <AlertCircle size={18} aria-hidden="true" /> : <Check size={18} aria-hidden="true" />}
      <span>{toast.message}</span>
      {toast.action && (
        <button
          type="button"
          className="toast-action"
          onClick={() => {
            toast.action!.onClick();
            onDismiss();
          }}
        >
          {toast.action.label}
        </button>
      )}
      <button
        type="button"
        className="toast-close"
        aria-label={error ? "Dismiss error" : "Dismiss notification"}
        onClick={onDismiss}
      >
        <X size={17} />
      </button>
    </div>
  );
}

export function ToastStack({ toasts, dismiss }: { toasts: Toast[]; dismiss: (id: number) => void }) {
  return createPortal(
    <div className="toast-stack" aria-live="polite">
      {toasts.map((t) => (
        <ToastView key={t.id} toast={t} onDismiss={() => dismiss(t.id)} />
      ))}
    </div>,
    document.body,
  );
}
