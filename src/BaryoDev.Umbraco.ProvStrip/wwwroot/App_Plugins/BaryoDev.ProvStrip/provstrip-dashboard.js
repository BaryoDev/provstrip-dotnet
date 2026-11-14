// The ProvStrip dashboard in the Media section: scan the library, then Delete or Skip per item, or Delete all.
// Nothing here changes a file until a Delete button is clicked.
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, nothing } from "@umbraco-cms/backoffice/external/lit";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { umbConfirmModal } from "@umbraco-cms/backoffice/modal";
import { STYLESHEET, apiFor, categoryOfLabel } from "./shared.js";

const PAGE = 100;
// The API serves at most this many results per call.
const MAX_TAKE = 500;
const POLL_MS = 2000;

export default class ProvStripDashboardElement extends UmbLitElement {
  static properties = {
    _scan: { state: true },
    _items: { state: true },
    _total: { state: true },
    _busy: { state: true },
    _error: { state: true },
    _notice: { state: true },
  };

  #auth;
  #api = apiFor(() => this.#auth);
  #timer;

  constructor() {
    super();
    this._scan = undefined;
    this._items = [];
    this._total = 0;
    this._busy = false;
    this._error = "";
    this._notice = "";
    this.consumeContext(UMB_AUTH_CONTEXT, (auth) => {
      this.#auth = auth;
      this.#refresh();
    });
  }

  disconnectedCallback() {
    super.disconnectedCallback();
    clearTimeout(this.#timer);
  }

  get #running() {
    return this._scan?.state === "Requested" || this._scan?.state === "Running";
  }

  async #refresh() {
    clearTimeout(this.#timer);
    try {
      this._scan = await this.#api.getScan();
      // Reload as many rows as are shown, in calls the API accepts.
      const want = Math.max(PAGE, this._items.length);
      const items = [];
      let total = 0;
      while (items.length < want) {
        const page = await this.#api.getResults(items.length, Math.min(MAX_TAKE, want - items.length));
        items.push(...page.items);
        total = page.total;
        if (page.items.length === 0) break;
      }
      this._items = items;
      this._total = total;
      this._error = "";
    } catch (e) {
      this._error = e.message;
    }
    if (this.#running && this.isConnected) this.#timer = setTimeout(() => this.#refresh(), POLL_MS);
  }

  async #more() {
    try {
      const page = await this.#api.getResults(this._items.length, PAGE);
      this._items = [...this._items, ...page.items];
      this._total = page.total;
    } catch (e) {
      this._error = e.message;
    }
  }

  async #run(work, notice = "") {
    this._busy = true;
    this._error = "";
    this._notice = "";
    try {
      await work();
      this._notice = notice;
    } catch (e) {
      this._error = e.message;
    } finally {
      this._busy = false;
      await this.#refresh();
    }
  }

  #scan = () => this.#run(() => this.#api.startScan());

  // Each row carries the hash of the file it was shown for, so a file changed since then is refused, not changed.
  #act = (action, item, category) => this.#run(() => this.#api[action](item.key, category, item.hash));

  #deleteAll = async () => {
    try {
      await umbConfirmModal(this, {
        headline: "Delete all",
        content: `Remove AI attribution and camera data from the ${this._total} listed items? Files are changed in place and there is no undo. Anything you skipped is kept.`,
        color: "danger",
        confirmLabel: "Delete all",
      });
    } catch {
      return; // Cancelled.
    }
    await this.#run(() => this.#api.removeAll(null), "Removing in the background. The list updates as items are cleaned; use Refresh to check.");
  };

  render() {
    return html`${STYLESHEET}
      <uui-box headline="ProvStrip">
        <div class="toolbar">
          <uui-button look="primary" label="Scan media library" ?disabled=${this._busy || this.#running} @click=${this.#scan}></uui-button>
          ${this.#progress()}
          <uui-button look="secondary" label="Refresh" ?disabled=${this._busy} @click=${() => this.#refresh()}></uui-button>
          ${this._items.length
            ? html`<uui-button look="primary" color="danger" label="Delete all" ?disabled=${this._busy} @click=${this.#deleteAll}></uui-button>`
            : nothing}
        </div>
        ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
        ${this._notice ? html`<p class="muted">${this._notice}</p>` : nothing}
        ${this.#table()}
      </uui-box>`;
  }

  #progress() {
    if (!this._scan || this._scan.state === "None") return html`<span class="muted">No scan yet.</span>`;
    // The background job picks a request up within seconds, or a few minutes after the site starts.
    if (this._scan.state === "Requested") return html`<span class="muted">Scan queued. It starts shortly.</span>`;
    return html`<span>Checked ${this._scan.checked} of ${this._scan.total}</span>`;
  }

  #table() {
    if (!this._items.length) return this._scan?.state === "Done" ? html`<p class="muted">No AI attribution or camera data found.</p>` : nothing;
    const folders = new Map();
    for (const item of [...this._items].sort((a, b) => a.folder.localeCompare(b.folder) || a.name.localeCompare(b.name))) {
      if (!folders.has(item.folder)) folders.set(item.folder, []);
      folders.get(item.folder).push(item);
    }
    return html`
      <table>
        <thead><tr><th>File</th><th>Found</th></tr></thead>
        <tbody>
          ${[...folders].map(
            ([folder, items]) => html`
              <tr><th class="folder" colspan="2">${folder || "Media root"}</th></tr>
              ${items.map((item) => this.#row(item))}
            `,
          )}
        </tbody>
      </table>
      ${this._items.length < this._total
        ? html`<uui-button look="secondary" label="Load more" @click=${() => this.#more()}></uui-button>`
        : nothing}`;
  }

  #row(item) {
    return html`<tr>
      <td>${item.name || item.key}</td>
      <td>
        ${item.labels.map((label) => {
          const category = categoryOfLabel(label);
          return html`<div class="row">
            <span class="label">${label}</span>
            ${category
              ? html`<span class="actions">
                  <uui-button look="primary" color="danger" label="Delete" ?disabled=${this._busy} @click=${() => this.#act("remove", item, category)}></uui-button>
                  <uui-button look="secondary" label="Skip" ?disabled=${this._busy} @click=${() => this.#act("skip", item, category)}></uui-button>
                </span>`
              : nothing}
          </div>`;
        })}
        ${item.needsLook
          ? html`<div class="row">
              <span class="label muted">Needs a look: ${item.needsLook}</span>
              ${item.hash
                ? html`<uui-button look="secondary" label="Skip" ?disabled=${this._busy} @click=${() => this.#act("skip", item, "Unknown")}></uui-button>`
                : nothing}
            </div>`
          : nothing}
      </td>
    </tr>`;
  }
}

customElements.define("provstrip-dashboard", ProvStripDashboardElement);
