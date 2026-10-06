import { useCallback, useEffect, useRef } from "react";

/**
 * A self-scheduling poll: run the task, wait for it to finish, then wait `interval` before the next run.
 *  - Never more than one run in flight; a run that is still going when the timer fires is not doubled up.
 *  - Paused while the tab is hidden; runs straight away when the tab becomes visible or the window regains focus
 *    (if the last run is older than `minGap`).
 *  - Each run gets an AbortSignal, aborted on unmount.
 * Returns `now()`, which runs the task immediately (or right after the current run) and restarts the timer.
 */
export function usePoll(
  task: (signal: AbortSignal) => Promise<void>,
  options: {
    /** Milliseconds between the end of one run and the start of the next; read again after every run. */
    interval: () => number;
    enabled?: boolean;
    /** Skip the visibility/focus refetch when the last run started less than this long ago. */
    minGap?: number;
  },
) {
  const taskRef = useRef(task);
  taskRef.current = task;
  const intervalRef = useRef(options.interval);
  intervalRef.current = options.interval;
  const control = useRef<{ now: () => void }>({ now: () => {} });
  const enabled = options.enabled ?? true;
  const minGap = options.minGap ?? 2000;

  useEffect(() => {
    if (!enabled) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let inFlight = false;
    let again = false;
    let lastStart = 0;
    const controller = new AbortController();

    const schedule = () => {
      clearTimeout(timer);
      if (stopped) return;
      timer = setTimeout(run, Math.max(0, intervalRef.current()));
    };
    async function run() {
      clearTimeout(timer);
      if (stopped) return;
      if (inFlight) {
        again = true;
        return;
      }
      // While hidden, wait for the tab to come back rather than polling in the background.
      if (document.hidden) return;
      inFlight = true;
      lastStart = Date.now();
      try {
        await taskRef.current(controller.signal);
      } catch {
        // The task reports its own errors; the loop keeps going.
      } finally {
        inFlight = false;
      }
      if (stopped) return;
      if (again) {
        again = false;
        void run();
      } else schedule();
    }
    const wake = () => {
      if (!document.hidden && !inFlight && Date.now() - lastStart >= minGap) void run();
    };
    control.current.now = () => void run();
    document.addEventListener("visibilitychange", wake);
    window.addEventListener("focus", wake);
    window.addEventListener("online", wake);
    void run();
    return () => {
      stopped = true;
      clearTimeout(timer);
      controller.abort();
      document.removeEventListener("visibilitychange", wake);
      window.removeEventListener("focus", wake);
      window.removeEventListener("online", wake);
      control.current.now = () => {};
    };
  }, [enabled, minGap]);

  return useCallback(() => control.current.now(), []);
}
