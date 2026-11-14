import { test } from "node:test";
import assert from "node:assert/strict";
import { createApi, ConflictError, ApiError } from "../../src/BaryoDev.Umbraco.ProvStrip/wwwroot/App_Plugins/BaryoDev.ProvStrip/api.js";

const BASE = "/umbraco/management/api/v1/baryodev/provstrip/";

/** A fetch that records each call and answers with the given status and JSON body. */
function fakeFetch(status = 200, body = {}) {
  const calls = [];
  const fetch = async (url, init = {}) => {
    calls.push({ url, method: init.method ?? "GET", headers: init.headers ?? {}, body: init.body === undefined ? undefined : JSON.parse(init.body) });
    return new Response(status === 202 && body === null ? null : JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
  };
  return { fetch, calls };
}

const api = (fetch) => createApi({ fetch, getToken: async () => "t0k3n" });

test("remove posts the category and hash with the token", async () => {
  const { fetch, calls } = fakeFetch(200, { key: "k", labels: [] });
  const result = await api(fetch).remove("k", "Camera", "abc");
  assert.deepEqual(result, { key: "k", labels: [] });
  assert.equal(calls.length, 1);
  assert.equal(calls[0].method, "POST");
  assert.equal(calls[0].url, BASE + "media/k/remove");
  assert.deepEqual(calls[0].body, { category: "Camera", hash: "abc" });
  assert.equal(calls[0].headers.Authorization, "Bearer t0k3n");
});

test("remove without a category sends null, meaning both", async () => {
  const { fetch, calls } = fakeFetch(200, {});
  await api(fetch).remove("k", undefined, "abc");
  assert.deepEqual(calls[0].body, { category: null, hash: "abc" });
});

test("a 409 surfaces as a stale-file error", async () => {
  const { fetch } = fakeFetch(409, { title: "File changed", detail: "The file changed since it was checked. Check it again." });
  await assert.rejects(api(fetch).remove("k", "Camera", "old"), (e) => e instanceof ConflictError && e.message === "The file changed since it was checked. Check it again.");
});

test("other failures carry the status and the problem's detail", async () => {
  const { fetch } = fakeFetch(403, {});
  await assert.rejects(api(fetch).getMedia("k"), (e) => e instanceof ApiError && e.status === 403);
});

test("each call hits its route", async () => {
  const { fetch, calls } = fakeFetch(200, {});
  const a = api(fetch);
  await a.getMedia("k");
  await a.skip("k", "Provenance", "h");
  await a.startScan();
  await a.getScan();
  await a.getResults(50, 25);
  await a.removeAll("Camera");
  assert.deepEqual(calls.map((c) => `${c.method} ${c.url.slice(BASE.length)}`), [
    "GET media/k",
    "POST media/k/skip",
    "POST scan",
    "GET scan",
    "GET scan/results?skip=50&take=25",
    "POST scan/remove-all",
  ]);
  assert.deepEqual(calls[1].body, { category: "Provenance", hash: "h" });
  assert.deepEqual(calls[5].body, { category: "Camera" });
});

test("a key is escaped into the path", async () => {
  const { fetch, calls } = fakeFetch(200, {});
  await api(fetch).getMedia("a/b");
  assert.equal(calls[0].url, BASE + "media/a%2Fb");
});
