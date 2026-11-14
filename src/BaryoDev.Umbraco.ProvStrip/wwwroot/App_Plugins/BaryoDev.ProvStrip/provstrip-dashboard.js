// The ProvStrip dashboard in the Media section: scan the library, then Delete or Skip per item, or Delete all.
// Nothing here changes a file until a Delete button is clicked.
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, nothing } from "@umbraco-cms/backoffice/external/lit";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { STYLESHEET, apiFor, categoryOfLabel } from "./shared.js";

const PAGE = 100;
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
      const page = await this.#api.getResults(0, Math.max(PAGE, this._items.length));
      this._items = page.items;
      this._total = page.total;
      this._error = "";
    } catch (e) {
      this._error = e.message;
    }
    if (this.#running) this.#timer = setTimeout(() => this.#refresh(), POLL_MS);
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

  // The results list carries no hash, so each action reads the item's current one first.
  #act = (action, key, category) =>
    this.#run(async () => {
      const media = await this.#api.getMedia(key);
      await this.#api[action](key, category, media.hash);
    });

  #deleteAll = () =>
    this.#run(() => this.#api.removeAll(null), "Removing in the background. The list updates as items are cleaned; use Refresh to check.");

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
                  <uui-button look="primary" color="danger" label="Delete" ?disabled=${this._busy} @click=${() => this.#act("remove", item.key, category)}></uui-button>
                  <uui-button look="secondary" label="Skip" ?disabled=${this._busy} @click=${() => this.#act("skip", item.key, category)}></uui-button>
                </span>`
              : nothing}
          </div>`;
        })}
        ${item.needsLook ? html`<div class="row muted">Needs a look: ${item.needsLook}</div>` : nothing}
      </td>
    </tr>`;
  }
}

customElements.define("provstrip-dashboard", ProvStripDashboardElement);
