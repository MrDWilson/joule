/**
 * Errors in plain words. The browser's own messages ("Failed to fetch", "Load failed", "NetworkError when attempting to
 * fetch resource", "Unexpected token '<'") mean nothing to a homeowner, so they never reach the screen.
 */

/** Thrown by the API client when the sign-in session has expired (a redirect to a login page, a 401 or an HTML reply; not a 403). */
export class SessionExpiredError extends Error {
  readonly sessionExpired = true;
  constructor() {
    super("Your session has expired. Sign in again to carry on.");
    this.name = "SessionExpiredError";
  }
}

/** Thrown by the API client when Joule's server could not be reached or took too long. */
export class OfflineError extends Error {
  readonly offline = true;
  constructor(
    readonly reason: "unreachable" | "timeout",
    /**
     * The status a gateway answered with in Joule's place (502/503/504, or a bare 500 from a proxy): the browser got
     * through, but Joule itself didn't answer. Usually a restart after a deploy.
     */
    readonly status?: number,
  ) {
    super(
      reason === "timeout"
        ? "Joule took too long to answer."
        : status
          ? "Joule isn't answering right now."
          : "Can't reach Joule's server.",
    );
    this.name = "OfflineError";
  }
}

/**
 * Statuses a reverse proxy (or the dev server's proxy) answers with while Joule is down or restarting. Joule itself
 * only uses them with its JSON { error } body ("Predbat is not configured", 503), which stays an ApiError.
 */
export const GATEWAY_STATUSES: readonly number[] = [500, 502, 503, 504];

/** True when a gateway answered in Joule's place: Joule is restarting or down, rather than refusing the request. */
export function isGatewayError(error: unknown): boolean {
  return error instanceof OfflineError && error.status != null;
}

const network = /failed to fetch|load failed|networkerror|network request failed|err_internet|err_network/i;

export function isOffline(error: unknown): boolean {
  if (error instanceof OfflineError) return true;
  if (
    error instanceof Error &&
    (error.name === "TypeError" || error.name === "AbortError") &&
    network.test(error.message)
  )
    return true;
  return error instanceof Error && error.name === "TimeoutError";
}

/** One sentence for the screen. */
export function plainError(error: unknown): string {
  if (error == null || error === "") return "Something went wrong.";
  if (typeof error === "string") return tidy(network.test(error) ? "Can't reach Joule's server." : error);
  if (error instanceof SessionExpiredError || error instanceof OfflineError) return error.message;
  if (error instanceof Error) {
    if (error.name === "AbortError" || error.name === "TimeoutError") return "Joule took too long to answer.";
    if (network.test(error.message)) return "Can't reach Joule's server.";
    if (error.name === "SyntaxError" || /unexpected token|is not valid json/i.test(error.message))
      return "Joule sent a reply this page couldn't read. Reload the page.";
    return tidy(error.message);
  }
  return "Something went wrong.";
}

/** Exactly one full stop at the end, so "retrying.. Retry" can't happen. */
function tidy(text: string) {
  const t = text.trim().replace(/\.+$/, "");
  return /[!?]$/.test(t) ? t : `${t}.`;
}
