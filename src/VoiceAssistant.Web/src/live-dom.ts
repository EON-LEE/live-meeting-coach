/**
 * DOM access for the live page. The panel can move into an always-on-top floating window (Document Picture-in-Picture), so
 * ids are looked up in the page first and then in the document that shows the panel now ({@link liveDocument}). Writes skip
 * unchanged values, so a render touches only what changed and never disturbs a selection or the screen reader.
 */
let current: Document = document;

/** The document that shows the live panel: the page, or the floating window it moved into. */
export function liveDocument(): Document { return current; }
export function setLiveDocument(value: Document): void { current = value; }

export const $ = <T extends HTMLElement = HTMLElement>(id: string): T => {
  const value = document.getElementById(id) ?? current.getElementById(id);
  if (!value) throw new Error(`Missing #${id}`);
  return value as T;
};

export function write(element: HTMLElement, text: string): void {
  if (element.textContent !== text) element.textContent = text;
}
export function setText(id: string, text: string): void { write($(id), text); }
export function setHidden(id: string, hidden: boolean): void {
  const element = $(id);
  if (element.hidden !== hidden) element.hidden = hidden;
}
export function setPressed(id: string, pressed: boolean): void {
  const element = $(id), value = String(pressed);
  if (element.getAttribute("aria-pressed") !== value) element.setAttribute("aria-pressed", value);
}
export function setTitle(element: HTMLElement, title: string): void {
  if (element.title !== title) element.title = title;
}
/** Sets or removes a `data-*` value; `undefined` removes it. */
export function setData(element: HTMLElement, key: string, value: string | undefined): void {
  if (element.dataset[key] === value) return;
  if (value === undefined) delete element.dataset[key]; else element.dataset[key] = value;
}

/** Whether the element can hold keyboard focus now: still in the panel, shown and enabled. */
export function canFocus(panel: HTMLElement, element: Element | null): element is HTMLElement {
  return element instanceof HTMLElement && panel.contains(element) && !element.matches(":disabled") && element.getClientRects().length > 0;
}
/**
 * Focus goes back to the element that opened the menu or the settings dialog, or to the panel when that element is gone.
 * Only closing them calls this: streaming updates never move focus, which may be in Teams or PowerPoint.
 */
export function returnFocus(panel: HTMLElement, opener: Element | null): void {
  (canFocus(panel, opener) ? opener : panel).focus({ preventScroll: true });
}
