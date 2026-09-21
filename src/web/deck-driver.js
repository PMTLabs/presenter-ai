// Drives a same-origin HTML deck inside an iframe through an auto-detected adapter,
// and reports navigation done by the deck itself (its own buttons/keys) via hashchange.

const ADAPTERS = {
  showFn: {
    detect: (win) => typeof win.show === 'function' && win.document.querySelectorAll('.slide').length > 0,
    goto: (win, i) => win.show(i),
    count: (win) => win.document.querySelectorAll('.slide').length,
    indexFromHash: (hash) => {
      const m = hash.match(/slide-(\d+)/);
      return m ? Number(m[1]) - 1 : null;
    },
  },
  reveal: {
    detect: (win) => Boolean(win.Reveal && typeof win.Reveal.slide === 'function' && (win.Reveal.isReady?.() ?? true)),
    goto: (win, i) => win.Reveal.slide(i, 0),
    count: (win) => win.Reveal.getHorizontalSlides?.().length ?? win.Reveal.getTotalSlides?.() ?? 0,
    indexFromHash: (hash) => {
      const m = hash.match(/^#\/(\d+)/);
      return m ? Number(m[1]) : null;
    },
  },
  sections: {
    detect: (win) => win.document.querySelectorAll('section.slide').length > 0,
    goto: (win, i) => {
      win.document.querySelectorAll('section.slide').forEach((el, k) => el.classList.toggle('active', k === i));
    },
    count: (win) => win.document.querySelectorAll('section.slide').length,
    indexFromHash: () => null,
  },
};

const AUTO_ORDER = ['showFn', 'reveal', 'sections'];

export class DeckDriver {
  constructor(iframe, { log = () => {}, onExternalNavigate = () => {} } = {}) {
    this.iframe = iframe;
    this.log = log;
    this.onExternalNavigate = onExternalNavigate;
    this.adapterName = null;
    this.adapter = null;
    this.currentIndex = 0;
    this._hashHandler = null;
  }

  get win() {
    return this.iframe.contentWindow;
  }

  /** Loads the deck URL and detects the adapter. Resolves with { adapter, count }. */
  load(url, { driver = 'auto', detectTimeoutMs = 3000 } = {}) {
    this.#detachHash();
    this.adapter = null;
    this.adapterName = null;
    return new Promise((resolve) => {
      const onLoad = async () => {
        this.iframe.removeEventListener('load', onLoad);
        const found = await this.#detect(driver, detectTimeoutMs);
        if (!found) {
          this.log('warn', `deck adapter: none matched (driver=${driver}); slides must be flipped by hand`);
          resolve({ adapter: null, count: 0 });
          return;
        }
        this.adapterName = found;
        this.adapter = ADAPTERS[found];
        const count = this.count();
        this.log('info', `deck adapter: ${found}, ${count} slides`);
        this.#attachHash();
        resolve({ adapter: found, count });
      };
      this.iframe.addEventListener('load', onLoad);
      this.iframe.src = url;
    });
  }

  async #detect(driver, timeoutMs) {
    const names = driver && driver !== 'auto' ? [driver] : AUTO_ORDER;
    const deadline = Date.now() + timeoutMs;
    // Scripts such as reveal.js may initialise asynchronously: poll briefly.
    while (Date.now() < deadline) {
      for (const name of names) {
        try {
          if (ADAPTERS[name]?.detect(this.win)) return name;
        } catch (err) {
          this.log('warn', `deck adapter ${name} detect failed: ${err.message} (is the deck same-origin?)`);
          return null;
        }
      }
      // "sections" always matches when present; only keep polling while a richer adapter might appear
      if (names.length === 1 || !names.includes('showFn')) break;
      await new Promise((r) => setTimeout(r, 100));
    }
    for (const name of names) {
      try {
        if (ADAPTERS[name]?.detect(this.win)) return name;
      } catch {}
    }
    return null;
  }

  #attachHash() {
    const win = this.win;
    this._hashHandler = () => {
      const idx = this.adapter?.indexFromHash(win.location.hash || '');
      if (idx == null || idx === this.currentIndex) return;
      this.currentIndex = idx;
      this.onExternalNavigate(idx);
    };
    win.addEventListener('hashchange', this._hashHandler);
  }

  #detachHash() {
    if (this._hashHandler && this.win) {
      try {
        this.win.removeEventListener('hashchange', this._hashHandler);
      } catch {}
    }
    this._hashHandler = null;
  }

  goto(i) {
    if (!this.adapter) return false;
    this.currentIndex = i; // set first so the resulting hashchange is recognised as ours
    try {
      this.adapter.goto(this.win, i);
      return true;
    } catch (err) {
      this.log('error', `deck goto(${i}) failed: ${err.message}`);
      return false;
    }
  }

  count() {
    if (!this.adapter) return 0;
    try {
      return this.adapter.count(this.win);
    } catch {
      return 0;
    }
  }
}
