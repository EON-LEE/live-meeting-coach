import { canFocus, returnFocus } from "./live-dom.js";

export interface MenuDeps {
  panel: HTMLElement;
  menu: HTMLElement;
  /** Brings the items' states up to date just before the menu shows. */
  refresh(): void;
  /** What an item with a `data-action` does. */
  run(action: string): void;
}
export interface LiveMenu {
  /** Hides the menu; focus returns to where it was opened from (Escape, a choice or a click outside), unless `refocus` is false. */
  close(refocus?: boolean): void;
}

/**
 * The panel's right-click menu: right-click, or Shift+F10 / the menu key on the focused panel or one of its buttons, opens it
 * where the pointer is; arrow keys, Home and End move through its items. It listens on the document that shows the panel, so it
 * also works in the floating window.
 */
export function createMenu({ panel, menu, refresh, run }: MenuDeps): LiveMenu {
  function position(x: number, y: number): void {
    const bounds = panel.getBoundingClientRect();
    const left = Math.max(4, Math.min(x, bounds.width - menu.offsetWidth - 4));
    const top = Math.max(4, Math.min(y, bounds.height - menu.offsetHeight - 4));
    menu.style.left = `${left}px`; menu.style.top = `${top}px`;
  }
  new ResizeObserver(() => {
    if (!menu.hidden) position(parseFloat(menu.style.left), parseFloat(menu.style.top));
  }).observe(panel);
  /** The items that can take focus: enabled and shown (the WebView2 host hides the floating-window item with CSS). */
  function items(): HTMLButtonElement[] {
    return [...menu.querySelectorAll<HTMLButtonElement>("button")].filter(button => !button.disabled && button.getClientRects().length > 0);
  }
  /** While the menu is open: the element it was opened from, and how to stop listening for what closes it. */
  let opened: { opener: Element | null; release(): void } | undefined;
  function open(x: number, y: number): void {
    refresh();
    menu.hidden = false;
    const bounds = panel.getBoundingClientRect();
    position(x - bounds.left, y - bounds.top);
    if (!opened) {
      const owner = panel.ownerDocument, opener = owner.activeElement;
      const onKey = (event: KeyboardEvent): void => { if (event.key === "Escape") close(); };
      const onPointer = (event: PointerEvent): void => {
        if (menu.contains(event.target as Node)) return;
        close();
        // A click on another control focuses that control; a click on anything else would leave focus nowhere.
        owner.defaultView?.setTimeout(() => {
          const active = owner.activeElement;
          if (menu.hidden && (!active || active === owner.body || active === panel)) returnFocus(panel, opener);
        });
      };
      owner.addEventListener("pointerdown", onPointer, true); owner.addEventListener("keydown", onKey, true);
      opened = { opener, release: () => {
        owner.removeEventListener("pointerdown", onPointer, true); owner.removeEventListener("keydown", onKey, true);
      } };
    }
    items()[0]?.focus();
  }
  function close(refocus = true): void {
    menu.hidden = true;
    const was = opened;
    if (!was) return;
    opened = undefined;
    was.release();
    if (refocus) returnFocus(panel, was.opener);
  }
  // Inside the open menu a right-click leaves it as it is.
  panel.addEventListener("contextmenu", event => {
    event.preventDefault();
    if (!menu.contains(event.target as Node)) open(event.clientX, event.clientY);
  });
  menu.addEventListener("keydown", event => {
    // Tab leaves the menu from where it was opened.
    if (event.key === "Tab") { close(); return; }
    const list = items(), last = list.length - 1;
    const index = list.indexOf(panel.ownerDocument.activeElement as HTMLButtonElement);
    const next = event.key === "ArrowDown" ? index < last ? index + 1 : 0
      : event.key === "ArrowUp" ? index > 0 ? index - 1 : last
      : event.key === "Home" ? 0 : event.key === "End" ? last : undefined;
    if (next === undefined) return;
    event.preventDefault();
    list[next]?.focus();
  });
  menu.addEventListener("click", event => {
    const button = (event.target as HTMLElement).closest<HTMLButtonElement>("button");
    if (!button) return;
    const opener = opened?.opener ?? null;
    // Every item closes the menu; the items LiveCoach.exe adds (opacity, minimize, exit) run their own action.
    close();
    if (button.dataset.action) run(button.dataset.action);
    // A choice can turn off the button the menu was opened from (stop turns off pause): focus then goes to the panel.
    const focused = panel.ownerDocument.activeElement;
    if (focused === opener ? !canFocus(panel, opener) : !focused || focused === panel.ownerDocument.body) returnFocus(panel, opener);
  });
  return { close };
}
