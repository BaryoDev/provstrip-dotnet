// Calls to the ProvStrip Management API. No imports, so it runs in the backoffice and under node's test runner.

const BASE = "/umbraco/management/api/v1/baryodev/provstrip/";

/** The server refused because something changed: the file since its check, or a scan already running. */
export class ConflictError extends Error {}

/** Any other failed call. `status` is the HTTP status. */
export class ApiError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

async function problemText(response, fallback) {
  try {
    const problem = await response.json();
    return problem?.detail || problem?.title || fallback;
  } catch {
    return fallback;
  }
}

/**
 * @param {{ fetch?: typeof fetch, getToken: () => Promise<string | undefined> }} options
 */
export function createApi({ fetch = globalThis.fetch.bind(globalThis), getToken }) {
  async function call(method, path, body) {
    const headers = { Authorization: `Bearer ${await getToken()}` };
    if (body !== undefined) headers["Content-Type"] = "application/json";
    const response = await fetch(BASE + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    if (response.status === 409) throw new ConflictError(await problemText(response, "The file changed. Check it again."));
    if (!response.ok) throw new ApiError(response.status, await problemText(response, `Request failed (${response.status}).`));
    const text = await response.text();
    return text ? JSON.parse(text) : null;
  }

  const media = (key) => `media/${encodeURIComponent(key)}`;

  return {
    getMedia: (key) => call("GET", media(key)),
    remove: (key, category, hash) => call("POST", `${media(key)}/remove`, { category: category ?? null, hash }),
    skip: (key, category, hash) => call("POST", `${media(key)}/skip`, { category, hash }),
    startScan: () => call("POST", "scan"),
    getScan: () => call("GET", "scan"),
    getResults: (skip = 0, take = 50) => call("GET", `scan/results?skip=${skip}&take=${take}`),
    removeAll: (category) => call("POST", "scan/remove-all", { category: category ?? null }),
  };
}
