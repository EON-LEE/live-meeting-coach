import { setLiveDocument } from "./live-dom.js";

interface DocumentPictureInPicture { requestWindow(options: { width: number; height: number }): Promise<Window> }

export interface FloatDeps {
  panel: HTMLElement;
  /** Where the panel lives on the page, and returns to. */
  stage: HTMLElement;
  /** The panel moved into the floating window `floating`. */
  opened(floating: Window): void;
  /** The floating window closed and the panel is back on the page. */
  returned(): void;
}
export interface LiveFloat {
  /** Whether this browser can float the panel (Edge/Chrome). */
  readonly supported: boolean;
  /** The floating window while the panel is in it. */
  readonly window: Window | undefined;
  /** Moves the panel into an always-on-top window, or back to the page. */
  toggle(): Promise<void>;
}

/** The always-on-top floating window (Document Picture-in-Picture). The panel moves into it with the page's styles. */
export function createFloat({ panel, stage, opened, returned }: FloatDeps): LiveFloat {
  let floatingWindow: Window | undefined;
  async function toggle(): Promise<void> {
    if (floatingWindow) { floatingWindow.close(); return; }
    const api = (window as unknown as { documentPictureInPicture?: DocumentPictureInPicture }).documentPictureInPicture;
    if (!api) return;
    const floating = await api.requestWindow({ width: 550, height: 860 });
    for (const sheet of [...document.styleSheets]) {
      try {
        const style = floating.document.createElement("style");
        style.textContent = [...sheet.cssRules].map(rule => rule.cssText).join("\n");
        floating.document.head.append(style);
      } catch {
        if (sheet.href) {
          const link = floating.document.createElement("link"); link.rel = "stylesheet"; link.href = sheet.href;
          floating.document.head.append(link);
        }
      }
    }
    floating.document.documentElement.lang = "ko";
    floating.document.title = "Live Coach";
    floating.document.body.className = "live-body live-floating";
    const floatingStage = floating.document.createElement("main");
    floatingStage.className = "live-stage";
    floatingStage.append(panel);
    floating.document.body.append(floatingStage);
    setLiveDocument(floating.document);
    floatingWindow = floating;
    floating.addEventListener("pagehide", () => {
      stage.append(panel); setLiveDocument(document); floatingWindow = undefined;
      returned();
    });
    opened(floating);
  }
  return { get supported() { return "documentPictureInPicture" in window; }, get window() { return floatingWindow; }, toggle };
}
