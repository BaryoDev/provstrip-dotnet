// The ProvStrip box on a media item: what was found, with Delete and Skip for each category.
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, nothing } from "@umbraco-cms/backoffice/external/lit";
import { UMB_MEDIA_WORKSPACE_CONTEXT } from "@umbraco-cms/backoffice/media";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { ConflictError } from "./api.js";
import { STYLESHEET, apiFor, labelledCategories } from "./shared.js";

export default class ProvStripMediaInfoElement extends UmbLitElement {
  static properties = {
    _model: { state: true },
    _error: { state: true },
    _busy: { state: true },
  };

  #auth;
  #key;
  #api = apiFor(() => this.#auth);

  constructor() {
    super();
    this._model = undefined;
    this._error = "";
    this._busy = false;
    this.consumeContext(UMB_AUTH_CONTEXT, (auth) => {
      this.#auth = auth;
      this.#load();
    });
    this.consumeContext(UMB_MEDIA_WORKSPACE_CONTEXT, (workspace) => {
      this.observe(workspace?.unique, (unique) => {
        this.#key = unique ?? undefined;
        this.#load();
      });
    });
  }

  async #load() {
    if (!this.#key || !this.#auth) return;
    this._error = "";
    try {
      this._model = await this.#api.getMedia(this.#key);
    } catch (e) {
      this._error = e.message;
    }
  }

  async #act(action, category) {
    if (!this._model?.hash) return;
    this._busy = true;
    this._error = "";
    try {
      this._model = await this.#api[action](this.#key, category, this._model.hash);
    } catch (e) {
      this._error = e.message;
      if (e instanceof ConflictError) await this.#load();
    } finally {
      this._busy = false;
    }
  }

  render() {
    return html`${STYLESHEET}
      <uui-box headline="ProvStrip">
        ${this._error ? html`<p class="error">${this._error}</p>` : nothing}
        ${this.#body()}
      </uui-box>`;
  }

  #body() {
    if (!this._model) return this._error ? nothing : html`<uui-loader-bar></uui-loader-bar>`;
    const rows = labelledCategories(this._model);
    return html`
      ${rows.map(
        ({ category, label }) => html`<div class="row">
          <span class="label">${label}</span>
          <uui-button look="primary" color="danger" label="Delete" ?disabled=${this._busy} @click=${() => this.#act("remove", category)}></uui-button>
          <uui-button look="secondary" label="Skip" ?disabled=${this._busy} @click=${() => this.#act("skip", category)}></uui-button>
        </div>`,
      )}
      ${this._model.needsLook ? html`<p>Needs a look: ${this._model.needsLook}</p>` : nothing}
      ${rows.length === 0 && !this._model.needsLook ? html`<p class="muted">No AI attribution or camera data found.</p>` : nothing}
    `;
  }
}

customElements.define("provstrip-media-info", ProvStripMediaInfoElement);
