import { GATEWAY_STATUSES, OfflineError, SessionExpiredError } from "./errors";

/**
 * The browser side of Joule's API. Every request carries the X-Joule-Request header (the server's CSRF guard) and,
 * when one was entered, the access key. Failures come back as typed errors:
 *   AccessKeyRequiredError  the server wants the application access key (show the key form)
 *   SessionExpiredError     a sign-in proxy redirected us, said 401, or sent a web page instead of JSON. A 403 is
 *                           deliberately not one: Joule's own CSRF/same-origin guard answers 403, and signing in
 *                           again would not fix that, so it surfaces as an ApiError with the server's message.
 *   OfflineError            the server could not be reached, or (when a timeout was asked for) took longer than it,
 *                           or a gateway answered in its place: 502/503/504, or a 500 without Joule's JSON error body
 *                           (a reverse proxy, or the dev proxy, while Joule restarts). The offline back-off applies.
 *
 * Only the background polls (state, measured figures) set a timeout, so a stuck poll can't block the next one.
 * Everything else, and every POST in particular, waits for the server: AI replies and reports call the model
 * synchronously and can take a minute or more, and the server enforces its own limits (90 s for a reply).
 *   ApiError                the server answered with an error message
 */

export class AccessKeyRequiredError extends Error {
  readonly accessKeyRequired = true;
  constructor() {
    super("An access key is required to connect.");
    this.name = "AccessKeyRequiredError";
  }
}
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/** Session storage key for the access key typed into the sign-in form. */
export const ACCESS_KEY_STORAGE = "joule-access";
/** The key's name before the rename to Joule; still read so a browser session open across the upgrade stays signed in. */
export const LEGACY_ACCESS_KEY_STORAGE = "predbat-access";

/** The access key saved for this browser session, from the current key or the pre-rename one. */
export function storedAccessKey(storage?: Pick<Storage, "getItem">): string | null {
  try {
    const from = storage ?? sessionStorage;
    return from.getItem(ACCESS_KEY_STORAGE) ?? from.getItem(LEGACY_ACCESS_KEY_STORAGE);
  } catch {
    return null;
  }
}

/** Saves the access key under the current name only. */
export function storeAccessKey(key: string, storage: Pick<Storage, "setItem"> = sessionStorage) {
  storage.setItem(ACCESS_KEY_STORAGE, key);
}
/** The abort deadline for background polls. Not applied unless a caller asks for it. */
export const POLL_TIMEOUT_MS = 20_000;

export interface RequestOptions {
  body?: unknown;
  signal?: AbortSignal;
  /** Abort after this long. Default: no client deadline (the server's own limits apply). */
  timeoutMs?: number;
  /** Sent as If-None-Match; a 304 reply comes back as notModified. */
  etag?: string | null;
}
export interface ApiResponse<T> {
  data: T;
  etag: string | null;
  notModified: boolean;
}

function headers(json: boolean, etag?: string | null): HeadersInit {
  const key = storedAccessKey();
  return {
    ...(json ? { "Content-Type": "application/json" } : {}),
    "X-Joule-Request": "1",
    ...(key ? { "X-Access-Key": key } : {}),
    ...(etag ? { "If-None-Match": etag } : {}),
  };
}

/** Links an outer abort signal and a timeout into one signal. */
function withTimeout(signal: AbortSignal | undefined, ms: number | undefined) {
  const controller = new AbortController();
  let timedOut = false;
  const timer =
    ms === undefined
      ? undefined
      : setTimeout(() => {
          timedOut = true;
          controller.abort();
        }, ms);
  const forward = () => controller.abort();
  if (signal) {
    if (signal.aborted) controller.abort();
    else signal.addEventListener("abort", forward, { once: true });
  }
  return {
    signal: controller.signal,
    timedOut: () => timedOut,
    done: () => {
      clearTimeout(timer);
      signal?.removeEventListener("abort", forward);
    },
  };
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<ApiResponse<T>> {
  const { body, etag } = options;
  const limit = withTimeout(options.signal, options.timeoutMs);
  let response: Response;
  try {
    response = await fetch("/api" + path, {
      method: body === undefined ? "GET" : "POST",
      headers: headers(body !== undefined, etag),
      // A sign-in proxy answers an expired session with a redirect to its login page; don't follow it.
      redirect: "manual",
      signal: limit.signal,
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
  } catch (e) {
    limit.done();
    if (limit.timedOut()) throw new OfflineError("timeout");
    // A caller's own abort (navigation, a newer poll) passes through untouched.
    if (options.signal?.aborted) throw e;
    throw new OfflineError("unreachable");
  }
  limit.done();
  if (
    response.type === "opaqueredirect" ||
    (response.status >= 300 && response.status < 400 && response.status !== 304)
  )
    throw new SessionExpiredError();
  if (response.status === 304) return { data: null as T, etag: etag ?? null, notModified: true };
  const type = response.headers.get("content-type") ?? "";
  if (response.status === 401) {
    const challenge = type.includes("json") ? await response.json().catch(() => null) : null;
    if (challenge?.authMode === "AccessKey") throw new AccessKeyRequiredError();
    throw Object.assign(new SessionExpiredError(), {
      message: "Access was denied by the server. Sign in again to carry on.",
    });
  }
  const gateway = GATEWAY_STATUSES.includes(response.status);
  // A web page where JSON was expected: a login page from a proxy, or an API this page doesn't know.
  if (type.includes("text/html") && !gateway) throw new SessionExpiredError();
  if (!response.ok) {
    const e = type.includes("text/html") ? null : await response.json().catch(() => null);
    // Joule's own errors carry a JSON { error }. A 5xx without one is a gateway (nginx, the dev proxy) answering in its
    // place, usually while it restarts after a deploy: that is "offline", with its back-off and banner, not stale data.
    if (gateway && !e?.error) throw new OfflineError("unreachable", response.status);
    throw new ApiError(e?.error || `The server couldn't do that (error ${response.status}).`, response.status);
  }
  const data = response.status === 204 ? (null as T) : ((await response.json()) as T);
  return { data, etag: response.headers.get("etag"), notModified: false };
}

/** The shape every component uses: GET without a body, POST with one. No client deadline. */
export async function api<T = unknown>(path: string, body?: unknown): Promise<T> {
  return (await request<T>(path, { body })).data;
}

/** A background poll's GET: aborted after POLL_TIMEOUT_MS (or by the caller's signal). */
export async function pollGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  return (await request<T>(path, { signal, timeoutMs: POLL_TIMEOUT_MS })).data;
}

/** Downloads a file from the API with the access key (for exports). */
export async function download(path: string, filename: string) {
  const response = await fetch("/api" + path, { headers: headers(false) });
  if (!response.ok) throw new ApiError("The download failed.", response.status);
  const url = URL.createObjectURL(await response.blob());
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}
