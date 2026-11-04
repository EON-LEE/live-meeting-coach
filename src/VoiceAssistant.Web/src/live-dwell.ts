import { viewedLimits, type ViewedItem } from "./feedback.js";

/**
 * How long each reply stayed on screen, for `response.viewed`. This is on-screen time, not attention: the overlay stays on
 * top while Teams has focus, so focus and blur are not used, and the page reports nothing while it is hidden. Only replies
 * the current connection issued are counted, since an id means nothing to another connection. Times come from the
 * injected clock (`performance.now()` in the page), never from timers.
 */
export class DwellTracker {
  private shown?: { id: string; since: number };
  /** Finished on-screen time per reply, waiting to be sent; the oldest go first if too many wait. */
  private readonly pending = new Map<string, number>();
  private readonly issuedIds = new Set<string>();
  private sentAt: number;

  constructor(private readonly now: () => number, private readonly intervalMs = 5000, private readonly keep = 100) {
    this.sentAt = now();
  }

  /** A reply id the current connection sent. */
  issued(id: string): void {
    this.issuedIds.delete(id); this.issuedIds.add(id);
    trim(this.issuedIds, this.keep);
  }

  /** The reply on screen now, or none: nothing displayed, the page hidden, or nothing to report to. */
  show(id: string | undefined): void {
    const next = id !== undefined && this.issuedIds.has(id) ? id : undefined;
    if (next === this.shown?.id) return;
    if (this.shown) this.add(this.shown.id, this.now() - this.shown.since);
    this.shown = next === undefined ? undefined : { id: next, since: this.now() };
  }

  /**
   * Batches of at most 20 items to send. Finished times wait until `intervalMs` passed since the last batch, unless `all`;
   * the reply still on screen counts once it is no longer shown.
   */
  take(all = false): ViewedItem[][] {
    const now = this.now();
    if (!this.pending.size || (!all && now - this.sentAt < this.intervalMs)) return [];
    this.sentAt = now;
    const items = [...this.pending].map(([responseId, visibleMs]) => ({ responseId, visibleMs }));
    this.pending.clear();
    const batches: ViewedItem[][] = [];
    for (let i = 0; i < items.length; i += viewedLimits.items) batches.push(items.slice(i, i + viewedLimits.items));
    return batches;
  }

  /** The connection is gone: what it issued and what was not sent yet is dropped. */
  drop(): void {
    this.shown = undefined;
    this.pending.clear();
    this.issuedIds.clear();
  }

  private add(id: string, ms: number): void {
    const visibleMs = Math.min(viewedLimits.maxMs, (this.pending.get(id) ?? 0) + Math.max(0, Math.round(ms)));
    if (visibleMs < 1) return;
    this.pending.delete(id); this.pending.set(id, visibleMs);
    trim(this.pending, this.keep);
  }
}

function trim(entries: Set<string> | Map<string, number>, size: number): void {
  for (const oldest of [...entries.keys()]) { if (entries.size <= size) break; entries.delete(oldest); }
}
