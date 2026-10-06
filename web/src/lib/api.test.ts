import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  ACCESS_KEY_STORAGE,
  api,
  ApiError,
  LEGACY_ACCESS_KEY_STORAGE,
  pollGet,
  POLL_TIMEOUT_MS,
  storeAccessKey,
  storedAccessKey,
} from "./api";
import { isOffline, OfflineError } from "./errors";

describe("a gateway answering for Joule", () => {
  const reply = (status: number, body: string, type: string) =>
    vi.fn(async () => new Response(body, { status, headers: { "content-type": type } }));

  it.each([502, 503, 504])("treats %i as Joule being offline, not an error or an expired session", async (status) => {
    vi.stubGlobal("fetch", reply(status, "<html>Bad gateway</html>", "text/html"));
    const outcome = (await pollGet("/state").catch((e) => e)) as Error;
    expect(outcome).toBeInstanceOf(OfflineError);
    expect(isOffline(outcome)).toBe(true);
    expect(outcome.message).toBe("Joule isn't answering right now.");
  });

  it("treats a bare 500 from a proxy as offline", async () => {
    vi.stubGlobal("fetch", reply(500, "", "text/plain"));
    const outcome = (await api("/state").catch((e) => e)) as Error;
    expect(outcome).toBeInstanceOf(OfflineError);
    expect(isOffline(outcome)).toBe(true);
  });

  it("keeps Joule's own 500 with a message as an error to show", async () => {
    vi.stubGlobal("fetch", reply(500, JSON.stringify({ error: "Predbat refused the change." }), "application/json"));
    const outcome = (await api("/collect", {}).catch((e) => e)) as Error;
    expect(outcome).toBeInstanceOf(ApiError);
    expect(isOffline(outcome)).toBe(false);
    expect(outcome.message).toBe("Predbat refused the change.");
  });

  it("keeps Joule's own 503 (Predbat not configured) as its message", async () => {
    vi.stubGlobal("fetch", reply(503, JSON.stringify({ error: "Predbat is not configured." }), "application/json"));
    const outcome = (await api("/collect", {}).catch((e) => e)) as Error;
    expect(outcome).toBeInstanceOf(ApiError);
    expect(isOffline(outcome)).toBe(false);
  });

  it("treats a gateway's JSON without Joule's error field as offline", async () => {
    vi.stubGlobal("fetch", reply(504, JSON.stringify({ message: "upstream timed out" }), "application/json"));
    expect(isOffline(await api("/state").catch((e) => e))).toBe(true);
  });
});

/** A fetch that never answers until aborted; resolves with JSON when `answer` is called. */
function pendingFetch() {
  let answer: (body: unknown) => void = () => {};
  const calls: RequestInit[] = [];
  const fetch = vi.fn((_url: string, init: RequestInit) => {
    calls.push(init);
    return new Promise<Response>((resolve, reject) => {
      init.signal?.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")));
      answer = (body) =>
        resolve(new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } }));
    });
  });
  return { fetch, calls, answer: (body: unknown) => answer(body) };
}

beforeEach(() => {
  vi.useFakeTimers();
  vi.stubGlobal("sessionStorage", { getItem: () => null });
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("client deadlines", () => {
  it("lets a POST (an AI reply) run longer than the poll deadline", async () => {
    const f = pendingFetch();
    vi.stubGlobal("fetch", f.fetch);
    const reply = api<{ outcome: string }>("/proposals/p1/reply", { note: "Why?" });
    await vi.advanceTimersByTimeAsync(POLL_TIMEOUT_MS * 4);
    expect(f.calls[0].signal?.aborted).toBe(false);
    f.answer({ outcome: "answered" });
    await expect(reply).resolves.toEqual({ outcome: "answered" });
  });

  it("lets an ordinary GET wait for the server", async () => {
    const f = pendingFetch();
    vi.stubGlobal("fetch", f.fetch);
    const read = api<{ ok: boolean }>("/files");
    await vi.advanceTimersByTimeAsync(POLL_TIMEOUT_MS * 2);
    f.answer({ ok: true });
    await expect(read).resolves.toEqual({ ok: true });
  });

  it("gives up on a background poll after the poll deadline", async () => {
    const f = pendingFetch();
    vi.stubGlobal("fetch", f.fetch);
    const poll = pollGet("/telemetry/status");
    const outcome = expect(poll).rejects.toSatisfy((e) => e instanceof OfflineError);
    await vi.advanceTimersByTimeAsync(POLL_TIMEOUT_MS - 1);
    expect(f.calls[0].signal?.aborted).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    await outcome;
  });
});

describe("the access key and request header", () => {
  const memory = (entries: Record<string, string> = {}) => {
    const items = new Map(Object.entries(entries));
    return {
      items,
      getItem: (k: string) => items.get(k) ?? null,
      setItem: (k: string, v: string) => void items.set(k, v),
    };
  };
  afterEach(() => vi.unstubAllGlobals());

  it("stores the key under the Joule name", () => {
    const storage = memory();
    storeAccessKey("a-key-of-sixteen-chars", storage);
    expect([...storage.items.keys()]).toEqual([ACCESS_KEY_STORAGE]);
    expect(ACCESS_KEY_STORAGE).toBe("joule-access");
  });

  it("still reads a key saved before the rename", () => {
    expect(storedAccessKey(memory({ [LEGACY_ACCESS_KEY_STORAGE]: "old-key" }))).toBe("old-key");
    expect(LEGACY_ACCESS_KEY_STORAGE).toBe("predbat-access");
  });

  it("prefers the new key over the pre-rename one", () => {
    expect(storedAccessKey(memory({ [LEGACY_ACCESS_KEY_STORAGE]: "old-key", [ACCESS_KEY_STORAGE]: "new-key" }))).toBe(
      "new-key",
    );
    expect(storedAccessKey(memory())).toBeNull();
  });

  it("sends X-Joule-Request and the saved key", async () => {
    vi.stubGlobal("sessionStorage", memory({ [LEGACY_ACCESS_KEY_STORAGE]: "old-key" }));
    const fetch = vi.fn(
      async (_url: string, _init: RequestInit) =>
        new Response("{}", { status: 200, headers: { "content-type": "application/json" } }),
    );
    vi.stubGlobal("fetch", fetch);
    await api("/mode", { mode: "Monitor" });
    const sent = new Headers(fetch.mock.calls[0][1].headers);
    expect(sent.get("X-Joule-Request")).toBe("1");
    expect(sent.get("X-PredbatAI-Request")).toBeNull();
    expect(sent.get("X-Access-Key")).toBe("old-key");
  });
});
