import type { APIResponse, Route } from '@playwright/test';

/**
 * Fetches the real response for an intercepted request without the client's If-None-Match, so a test that rewrites the
 * body always gets a full 200 to work from rather than an empty 304 (the API tags /api/state and other polls with ETags).
 */
export async function fetchFresh(route: Route, options: { url?: string } = {}): Promise<APIResponse> {
  const headers = { ...route.request().headers() };
  delete headers['if-none-match'];
  return route.fetch({ ...options, headers });
}

/** Fulfils with a rewritten JSON body. The server's ETag described the original body, so it is dropped. */
export async function fulfillRewritten(route: Route, response: APIResponse, json: unknown) {
  const headers = { ...response.headers() };
  delete headers.etag;
  delete headers['content-length'];
  delete headers['content-encoding'];
  await route.fulfill({ status: response.status(), headers, json });
}
