const adapters = {
  showFn: {
    detect: (w: Window) =>
      typeof (w as Window & { show?: unknown }).show === "function" &&
      w.document.querySelectorAll(".slide").length > 0,
    goto: (w: Window, i: number) =>
      (w as Window & { show: (n: number) => void }).show(i),
    count: (w: Window) => w.document.querySelectorAll(".slide").length,
    index: (hash: string) => {
      const m = hash.match(/slide-(\d+)/);
      return m ? Number(m[1]) - 1 : null;
    },
  },
  reveal: {
    detect: (w: Window) => {
      const r = (
        w as Window & { Reveal?: { slide?: unknown; isReady?: () => boolean } }
      ).Reveal;
      return Boolean(
        r && typeof r.slide === "function" && (r.isReady?.() ?? true),
      );
    },
    goto: (w: Window, i: number) =>
      (
        w as Window & { Reveal: { slide: (x: number, y: number) => void } }
      ).Reveal.slide(i, 0),
    count: (w: Window) => {
      const r = (
        w as Window & {
          Reveal: {
            getHorizontalSlides?: () => Element[];
            getTotalSlides?: () => number;
          };
        }
      ).Reveal;
      return r.getHorizontalSlides?.().length ?? r.getTotalSlides?.() ?? 0;
    },
    index: (h: string) => {
      const m = h.match(/^#\/(\d+)/);
      return m ? Number(m[1]) : null;
    },
  },
  sections: {
    detect: (w: Window) =>
      w.document.querySelectorAll("section.slide").length > 0,
    goto: (w: Window, i: number) =>
      w.document
        .querySelectorAll("section.slide")
        .forEach((el, n) => el.classList.toggle("active", n === i)),
    count: (w: Window) => w.document.querySelectorAll("section.slide").length,
    index: () => null,
  },
};
type AdapterName = keyof typeof adapters;
export class DeckDriver {
  adapterName: AdapterName | null = null;
  private adapter: (typeof adapters)[AdapterName] | null = null;
  currentIndex = 0;
  private hashHandler: (() => void) | null = null;
  constructor(
    private iframe: HTMLIFrameElement,
    private options: {
      log?: (level: string, message: string) => void;
      onExternalNavigate?: (index: number) => void;
    } = {},
  ) {}
  private get win() {
    return this.iframe.contentWindow!;
  }
  private log(level: string, msg: string) {
    this.options.log?.(level, msg);
  }
  load(
    url: string,
    {
      driver = "auto",
      detectTimeoutMs = 3000,
    }: { driver?: string; detectTimeoutMs?: number } = {},
  ) {
    this.detach();
    this.adapter = null;
    this.adapterName = null;
    return new Promise<{ adapter: AdapterName | null; count: number }>(
      (resolve) => {
        const loaded = async () => {
          this.iframe.removeEventListener("load", loaded);
          const name = await this.detect(driver, detectTimeoutMs);
          if (!name) {
            this.log(
              "warn",
              `deck adapter: none matched (driver=${driver}); slides must be flipped by hand`,
            );
            resolve({ adapter: null, count: 0 });
            return;
          }
          this.adapterName = name;
          this.adapter = adapters[name];
          const count = this.count();
          this.log("info", `deck adapter: ${name}, ${count} slides`);
          this.attach();
          resolve({ adapter: name, count });
        };
        this.iframe.addEventListener("load", loaded);
        this.iframe.src = url;
      },
    );
  }
  private async detect(driver: string, timeout: number) {
    const names =
      driver !== "auto" ? [driver] : ["showFn", "reveal", "sections"];
    const until = Date.now() + timeout;
    while (Date.now() < until) {
      for (const name of names)
        try {
          if (adapters[name as AdapterName]?.detect(this.win))
            return name as AdapterName;
        } catch (e) {
          this.log(
            "warn",
            `deck adapter ${name} detect failed: ${(e as Error).message} (is the deck same-origin?)`,
          );
          return null;
        }
      if (names.length === 1 || !names.includes("showFn")) break;
      await new Promise((r) => setTimeout(r, 100));
    }
    return null;
  }
  private attach() {
    this.hashHandler = () => {
      const index = this.adapter?.index(this.win.location.hash) ?? null;
      if (index !== null && index !== this.currentIndex) {
        this.currentIndex = index;
        this.options.onExternalNavigate?.(index);
      }
    };
    this.win.addEventListener("hashchange", this.hashHandler);
  }
  private detach() {
    try {
      if (this.hashHandler)
        this.win.removeEventListener("hashchange", this.hashHandler);
    } catch {
      /* cross-origin */
    }
    this.hashHandler = null;
  }
  goto(index: number) {
    if (!this.adapter) return false;
    this.currentIndex = index;
    try {
      this.adapter.goto(this.win, index);
      return true;
    } catch (e) {
      this.log("error", `deck goto(${index}) failed: ${(e as Error).message}`);
      return false;
    }
  }
  count() {
    try {
      return this.adapter?.count(this.win) ?? 0;
    } catch {
      return 0;
    }
  }
}
