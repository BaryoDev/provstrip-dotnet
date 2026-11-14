// Pieces both elements use. Plain JavaScript, no bundler: the backoffice import map resolves @umbraco-cms paths.
import { html } from "@umbraco-cms/backoffice/external/lit";
import { createApi } from "./api.js";

export const STYLESHEET = html`<link rel="stylesheet" href="/App_Plugins/BaryoDev.ProvStrip/provstrip.css" />`;

export const CATEGORIES = ["Provenance", "Camera"];

/** An API client whose token comes from the backoffice auth context, read at call time. */
export function apiFor(getAuthContext) {
  return createApi({
    getToken: async () => {
      const auth = getAuthContext();
      if (!auth) throw new Error("Not signed in.");
      return auth.getLatestToken();
    },
  });
}

/**
 * Pairs each label with its category. The server sends labels in category order (AI attribution, then camera data),
 * one per category that still has an Open finding.
 */
export function labelledCategories(model) {
  const open = CATEGORIES.filter((c) => (model?.findings ?? []).some((f) => f.category === c && f.status === "Open"));
  return open.map((category, i) => ({ category, label: model.labels?.[i] ?? category }));
}

/** The category a scan-result label belongs to, from the wording the server uses. */
export function categoryOfLabel(label) {
  if (label.startsWith("AI attribution")) return "Provenance";
  if (label.startsWith("Camera data")) return "Camera";
  return undefined;
}
